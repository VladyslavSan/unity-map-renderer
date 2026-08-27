using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// The native filter VM's error taxonomy. Burst forbids exceptions, so every erroring opcode threads
    /// a code instead of throwing; a non-<see cref="None"/> code halts evaluation immediately (sticky —
    /// later steps never run) and always maps the filter outcome to <c>matched = false</c> — the same
    /// outcome <c>CompiledFilter.Matches</c> gives a caught <c>ExpressionEvaluationException</c>.
    /// <see cref="StackOverflow"/> and <see cref="StepBudget"/> are defensive: compile-time-impossible for
    /// a <see cref="NativeFilterCompiler"/>-accepted program (bounded stack depth / op count), reachable
    /// only if that guarantee is ever broken — and even then map to exclude, never a silent include.
    /// </summary>
    internal enum NativeFilterError : byte
    {
        None,

        /// <summary><c>!</c>/<c>all</c> applied to a non-Boolean argument — mirrors <c>Value.AsBool</c>'s
        /// throw.</summary>
        NonBoolean,

        /// <summary>No feature in context — mirrors <c>FeatureKeyExpression</c>/<c>FeatureData</c>'s
        /// throw. Unreachable via <see cref="NativeFilterEvaluator"/> (its tag slice always names a real
        /// feature); mapped for completeness with the managed error taxonomy.</summary>
        NoFeature,

        /// <summary>Defensive: the VM operand stack over- or under-flowed.</summary>
        StackOverflow,

        /// <summary>Defensive: the program counter exceeded its step budget.</summary>
        StepBudget
    }

    /// <summary>
    /// The Burst-compiled opcode VM: evaluates one <see cref="NativeFilterProgram"/> against one feature's
    /// native columns. A post-order stack machine with real short-circuit jumps for <c>all</c> (design doc
    /// §5.8) over a bounded <see cref="FixedList128Bytes{T}"/> operand stack — never throws; see
    /// <see cref="NativeFilterError"/> for the threaded-error-code contract this job exists to prove
    /// compiles cleanly under Burst.
    ///
    /// <para>Declared here rather than beside the rest of the VM in <c>MapRenderer.Jobs.Expressions</c>
    /// because its columns (<see cref="Values"/>) are <see cref="MvtValueNative"/> — a format-named type a
    /// non-decoder-folder production type may not name in a member signature (see
    /// <c>NeutralGeometryPathTests</c>); <see cref="Mvt"/> is a decoder folder. <see cref="NativeFilterEvaluator"/>
    /// and <see cref="NativeFilterProgram"/>'s MVT rebind live here for the same reason.</para>
    /// </summary>
    [BurstCompile]
    internal struct NativeFilterEvalJob : IJob
    {
        public FixedList512Bytes<NativeFilterOp> Program;

        /// <summary>The rebound <c>slot→id</c> array: <see cref="NativeFilterProgram.KeyNames"/> ids
        /// first, then <see cref="NativeFilterProgram.LiteralStrings"/> ids (see
        /// <see cref="NativeFilterRebind.Rebind"/>).</summary>
        [ReadOnly] public NativeArray<int> Binding;

        /// <summary>The owning tile-layer's shared (keyIdx,valIdx) tag words — borrowed, never disposed
        /// here (see <see cref="MvtLayerPropertyResolver.TagWords"/>).</summary>
        [ReadOnly] public NativeArray<uint> TagWords;

        /// <summary>The owning tile-layer's shared decoded value table — borrowed (see
        /// <see cref="MvtLayerPropertyResolver.Values"/>).</summary>
        [ReadOnly] public NativeArray<MvtValueNative> Values;

        /// <summary>This feature's tag-pair slice into <see cref="TagWords"/>: start index and word count
        /// (not pair count) — supplied by the caller from <see cref="INativeFilterColumns.TryGetFeatureSlice"/>.</summary>
        public int TagOffset;
        public int TagCount;

        /// <summary>This feature's <c>TileGeometryType</c>, as an int.</summary>
        public int GeometryKind;

        /// <summary>Length-1 output: 1 iff the filter matched (meaningful only when
        /// <see cref="ResultError"/>[0] is <see cref="NativeFilterError.None"/>).</summary>
        public NativeArray<byte> ResultMatched;

        /// <summary>Length-1 output: the <see cref="NativeFilterError"/> code, as a byte.</summary>
        public NativeArray<byte> ResultError;

        public void Execute()
        {
            FixedList128Bytes<NativeValue> stack = default;
            NativeFilterError error = NativeFilterError.None;
            int pc = 0;

            // pc only ever increases (by 1, or by a strictly-forward AllStep jump), so this loop is
            // bounded by Program.Length steps — no separate step counter needed.
            while (pc < Program.Length && error == NativeFilterError.None)
            {
                NativeFilterOp op = Program[pc];
                switch (op.Op)
                {
                    case NativeOp.LitNum:
                        error = Push(ref stack, NativeValue.Number(op.Immediate));
                        pc++;
                        break;

                    case NativeOp.LitBool:
                        error = Push(ref stack, NativeValue.Bool(op.Operand != 0));
                        pc++;
                        break;

                    case NativeOp.LitStr:
                        error = Push(ref stack, NativeValue.String(Binding[op.Operand]));
                        pc++;
                        break;

                    case NativeOp.Get:
                    {
                        int keyIndex = Binding[op.Operand];
                        error = Push(ref stack, ReadTag(keyIndex));
                        pc++;
                        break;
                    }

                    case NativeOp.GeomEq:
                    {
                        bool eq = GeometryKind == op.Operand;
                        bool negate = op.Immediate != 0.0;
                        error = Push(ref stack, NativeValue.Bool(negate ? !eq : eq));
                        pc++;
                        break;
                    }

                    case NativeOp.Eq:
                    {
                        if (!Pop(ref stack, out NativeValue right)) { error = NativeFilterError.StackOverflow; break; }
                        if (!Pop(ref stack, out NativeValue left)) { error = NativeFilterError.StackOverflow; break; }
                        bool eq = NativeValue.NativeEquals(left, right);
                        bool negate = op.Immediate != 0.0;
                        error = Push(ref stack, NativeValue.Bool(negate ? !eq : eq));
                        pc++;
                        break;
                    }

                    case NativeOp.Not:
                    {
                        if (!Pop(ref stack, out NativeValue arg)) { error = NativeFilterError.StackOverflow; break; }
                        if (arg.Type != ValueType.Boolean) { error = NativeFilterError.NonBoolean; break; }
                        error = Push(ref stack, NativeValue.Bool(!arg.BoolValue));
                        pc++;
                        break;
                    }

                    case NativeOp.AllStep:
                    {
                        if (!Pop(ref stack, out NativeValue arg)) { error = NativeFilterError.StackOverflow; break; }
                        if (arg.Type != ValueType.Boolean) { error = NativeFilterError.NonBoolean; break; }
                        if (arg.BoolValue)
                        {
                            pc++;
                        }
                        else
                        {
                            error = Push(ref stack, NativeValue.Bool(false));
                            pc = op.Operand;
                        }
                        break;
                    }

                    case NativeOp.PushTrue:
                        error = Push(ref stack, NativeValue.Bool(true));
                        pc++;
                        break;

                    default:
                        // Unreachable: every op a NativeFilterCompiler-accepted program emits is one of the
                        // cases above. Mapped to exclude, never a silent include, matching NativeFilterError's
                        // no-fall-through-include contract.
                        error = NativeFilterError.StepBudget;
                        break;
                }
            }

            byte matched = 0;
            if (error == NativeFilterError.None)
            {
                // No top-level coercion: the compiler only accepts a statically-Boolean root (design doc
                // §5.4), so a clean run leaves exactly one Boolean on the stack.
                if (stack.Length == 1 && stack[0].Type == ValueType.Boolean)
                    matched = stack[0].BoolValue ? (byte)1 : (byte)0;
                else
                    error = NativeFilterError.StackOverflow; // defensive: unreachable for an accepted program.
            }

            ResultMatched[0] = matched;
            ResultError[0] = (byte)error;
        }

        /// <summary>Mirrors <see cref="DensePropertyStore.TryGetByKeyIndex"/> verbatim: walks this
        /// feature's tag pairs backward, returning the first (= last in tag order) whose key index matches
        /// and whose value index is in range. Bounded by this feature's own tag-pair count (design doc
        /// §5.9).</summary>
        private NativeValue ReadTag(int keyIndex)
        {
            if (keyIndex < 0) return NativeValue.Null;
            int pairCount = TagCount / 2;
            for (int i = pairCount - 1; i >= 0; i--)
            {
                if ((int)TagWords[TagOffset + i * 2] != keyIndex) continue;
                int valIdx = (int)TagWords[TagOffset + i * 2 + 1];
                if (valIdx < 0 || valIdx >= Values.Length) continue;
                return Values[valIdx].ToNativeValue();
            }
            return NativeValue.Null;
        }

        /// <summary>Pushes iff there is room; never lets the FixedList's own capacity check fire — that
        /// check throws, which a Burst job cannot do (the reason this stage exists).</summary>
        private static NativeFilterError Push(ref FixedList128Bytes<NativeValue> stack, NativeValue value)
        {
            if (stack.Length >= stack.Capacity) return NativeFilterError.StackOverflow;
            stack.Add(value);
            return NativeFilterError.None;
        }

        private static bool Pop(ref FixedList128Bytes<NativeValue> stack, out NativeValue value)
        {
            if (stack.Length == 0) { value = default; return false; }
            value = stack[stack.Length - 1];
            stack.Length -= 1;
            return true;
        }
    }
}
