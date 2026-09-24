using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// The native filter VM's error taxonomy. Burst forbids exceptions, so an erroring opcode returns a code;
    /// a non-<c>None</c> code halts evaluation and maps to <c>matched = false</c>, as <c>CompiledFilter.Matches</c>
    /// does for a caught <c>ExpressionEvaluationException</c>. <c>StackOverflow</c> and <c>StepBudget</c> are
    /// defensive: a <see cref="NativeFilterCompiler"/>-accepted program cannot reach them, and they exclude.
    /// </summary>
    internal enum NativeFilterError : byte
    {
        None,

        /// <summary><c>!</c>/<c>all</c> applied to a non-Boolean argument — mirrors <c>Value.AsBool</c>'s
        /// throw.</summary>
        NonBoolean,

        /// <summary>No feature in context — mirrors <c>FeatureKeyExpression</c>/<c>FeatureData</c>'s
        /// throw. Unreachable via <see cref="NativeFilterEvaluationJob"/> (every ordinal in
        /// <c>[0, FeatureCount)</c> names a real feature); mapped for completeness with the managed error
        /// taxonomy.</summary>
        NoFeature,

        /// <summary>Defensive: the VM operand stack over- or under-flowed.</summary>
        StackOverflow,

        /// <summary>Defensive: the program counter exceeded its step budget.</summary>
        StepBudget,

        /// <summary>An ordered comparison (<c>&lt;</c>/<c>&lt;=</c>/<c>&gt;</c>/<c>&gt;=</c>) whose operands
        /// were not both <see cref="ValueType.Number"/> at runtime (a <c>get</c> that resolved to a string
        /// or was absent) — mirrors <c>DecisionOps.CompareValues</c>'s type-mismatch throw, which
        /// <c>CompiledFilter</c> catches into an exclude.</summary>
        NonComparable
    }

    /// <summary>
    /// The Burst opcode VM: evaluates one <see cref="NativeFilterProgram"/> against every feature of one
    /// tile-layer in one dispatch. It is a post-order stack machine with short-circuit jumps for <c>all</c>,
    /// over a bounded operand stack, and it never throws (see <see cref="NativeFilterError"/>). It runs via
    /// <c>RunByRef</c> because its caller is an <c>IWorkScheduler</c> worker, where <c>Schedule</c> is illegal.
    /// It lives in <c>Mvt</c> because it names <see cref="MvtValueNative"/> (see <c>NeutralGeometryPathTests</c>).
    /// </summary>
    [BurstCompile]
    internal struct NativeFilterEvaluationJob : IJob
    {
        public FixedList512Bytes<NativeFilterOperation> Program;

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

        /// <summary>Per-feature start index into <c>TagWords</c>, by ordinal (see
        /// <see cref="INativeFilterColumns.TagOffsets"/>).</summary>
        [ReadOnly] public NativeArray<int> TagOffsets;

        /// <summary>Per-feature word count into <c>TagWords</c> (not pair count), by ordinal (see
        /// <see cref="INativeFilterColumns.TagLengths"/>).</summary>
        [ReadOnly] public NativeArray<int> TagLengths;

        /// <summary>Per-feature <c>TileGeometryType</c>, as an int, by ordinal.</summary>
        [ReadOnly] public NativeArray<int> Kinds;

        /// <summary>The number of features to evaluate, starting at ordinal 0 — the loop bound
        /// <see cref="Execute"/> runs over.</summary>
        public int FeatureCount;

        /// <summary>Per-feature output, by ordinal: 1 iff that feature's filter matched (meaningful only
        /// when <c>ResultError</c> at the same ordinal is <see cref="NativeFilterError.None"/>).</summary>
        public NativeArray<byte> ResultMatched;

        /// <summary>Per-feature output, by ordinal: the <see cref="NativeFilterError"/> code, as a byte.</summary>
        public NativeArray<byte> ResultError;

        public void Execute()
        {
            for (int featureIndex = 0; featureIndex < FeatureCount; featureIndex++)
            {
                // Declared inside the loop, so one feature's stack or error never reaches the next. Pinned by
                // NativeFilterVmTests.MatchAll_ErrorOnOneFeature_DoesNotExcludeTheNext and siblings.
                FixedList128Bytes<NativeValue> stack = default;
                NativeFilterError error = NativeFilterError.None;
                int programCounter = 0;
                int tagOffset = TagOffsets[featureIndex];
                int tagCount = TagLengths[featureIndex];
                int geometryKind = Kinds[featureIndex];

                // programCounter only ever increases (by 1, or by a strictly-forward AllStep jump), so this
                // loop is bounded by Program.Length steps — no separate step counter needed.
                while (programCounter < Program.Length && error == NativeFilterError.None)
                {
                    NativeFilterOperation operation = Program[programCounter];
                    switch (operation.Operation)
                    {
                        case NativeOperation.LiteralNumber:
                            error = Push(ref stack, NativeValue.Numeric(operation.Immediate));
                            programCounter++;
                            break;

                        case NativeOperation.LiteralBoolean:
                            error = Push(ref stack, NativeValue.Bool(operation.Operand != 0));
                            programCounter++;
                            break;

                        case NativeOperation.LiteralString:
                            error = Push(ref stack, NativeValue.String(Binding[operation.Operand]));
                            programCounter++;
                            break;

                        case NativeOperation.Get:
                        {
                            int keyIndex = Binding[operation.Operand];
                            error = Push(ref stack, ReadTag(tagOffset, tagCount, keyIndex, out _));
                            programCounter++;
                            break;
                        }

                        case NativeOperation.Has:
                        {
                            int keyIndex = Binding[operation.Operand];
                            ReadTag(tagOffset, tagCount, keyIndex, out bool present);
                            error = Push(ref stack, NativeValue.Bool(present));
                            programCounter++;
                            break;
                        }

                        case NativeOperation.GeometryEqual:
                        {
                            bool equal = geometryKind == operation.Operand;
                            bool negate = operation.Immediate != 0.0;
                            error = Push(ref stack, NativeValue.Bool(negate ? !equal : equal));
                            programCounter++;
                            break;
                        }

                        case NativeOperation.Equal:
                        {
                            if (!Pop(ref stack, out NativeValue right)) { error = NativeFilterError.StackOverflow; break; }
                            if (!Pop(ref stack, out NativeValue left)) { error = NativeFilterError.StackOverflow; break; }
                            bool equal = NativeValue.NativeEquals(left, right);
                            bool negate = operation.Immediate != 0.0;
                            error = Push(ref stack, NativeValue.Bool(negate ? !equal : equal));
                            programCounter++;
                            break;
                        }

                        case NativeOperation.Compare:
                        {
                            if (!Pop(ref stack, out NativeValue right)) { error = NativeFilterError.StackOverflow; break; }
                            if (!Pop(ref stack, out NativeValue left)) { error = NativeFilterError.StackOverflow; break; }
                            if (!NativeValue.TryCompare(left, right, out int comparison)) { error = NativeFilterError.NonComparable; break; }
                            bool result;
                            switch (operation.Operand)
                            {
                                case 0: result = comparison < 0; break;
                                case 1: result = comparison <= 0; break;
                                case 2: result = comparison > 0; break;
                                default: result = comparison >= 0; break;
                            }
                            error = Push(ref stack, NativeValue.Bool(result));
                            programCounter++;
                            break;
                        }

                        case NativeOperation.Not:
                        {
                            if (!Pop(ref stack, out NativeValue arg)) { error = NativeFilterError.StackOverflow; break; }
                            if (arg.Type != ValueType.Boolean) { error = NativeFilterError.NonBoolean; break; }
                            error = Push(ref stack, NativeValue.Bool(!arg.BoolValue));
                            programCounter++;
                            break;
                        }

                        case NativeOperation.AllStep:
                        {
                            if (!Pop(ref stack, out NativeValue arg)) { error = NativeFilterError.StackOverflow; break; }
                            if (arg.Type != ValueType.Boolean) { error = NativeFilterError.NonBoolean; break; }
                            if (arg.BoolValue)
                            {
                                programCounter++;
                            }
                            else
                            {
                                error = Push(ref stack, NativeValue.Bool(false));
                                programCounter = operation.Operand;
                            }
                            break;
                        }

                        case NativeOperation.PushTrue:
                            error = Push(ref stack, NativeValue.Bool(true));
                            programCounter++;
                            break;

                        case NativeOperation.InStringSet:
                        {
                            if (!Pop(ref stack, out NativeValue v)) { error = NativeFilterError.StackOverflow; break; }
                            bool member = false;
                            if (v.Type == ValueType.String)
                            {
                                int start = operation.Operand;
                                int count = (int)operation.Immediate;
                                for (int i = 0; i < count; i++)
                                    if (v.StringId == Binding[start + i]) { member = true; break; }
                            }
                            error = Push(ref stack, NativeValue.Bool(member));
                            programCounter++;
                            break;
                        }

                        default:
                            // Unreachable for a NativeFilterCompiler-accepted program; mapped to exclude, never a
                            // silent include.
                            error = NativeFilterError.StepBudget;
                            break;
                    }
                }

                byte matched = 0;
                if (error == NativeFilterError.None)
                {
                    // No top-level coercion: the compiler only accepts a statically-Boolean root, so a
                    // clean run leaves exactly one Boolean on the stack.
                    if (stack.Length == 1 && stack[0].Type == ValueType.Boolean)
                        matched = stack[0].BoolValue ? (byte)1 : (byte)0;
                    else
                        error = NativeFilterError.StackOverflow; // defensive: unreachable for an accepted program.
                }

                ResultMatched[featureIndex] = matched;
                ResultError[featureIndex] = (byte)error;
            }
        }

        /// <summary>Mirrors <see cref="DensePropertyStore.TryGetByKeyIndex"/>: the backward tag-pair scan.
        /// Non-obvious why: <paramref name="found"/> is set when the pair exists, whatever the value type,
        /// because <see cref="MvtDecoder"/> stores a present Null for a Value with no recognized field and
        /// managed <c>has</c> reports that key present. Only <c>has</c> reads it; <c>get</c> yields Null for
        /// both cases.</summary>
        private NativeValue ReadTag(int tagOffset, int tagCount, int keyIndex, out bool found)
        {
            found = false;
            if (keyIndex < 0) return NativeValue.Null;
            int pairCount = tagCount / 2;
            for (int i = pairCount - 1; i >= 0; i--)
            {
                if ((int)TagWords[tagOffset + i * 2] != keyIndex) continue;
                int valIdx = (int)TagWords[tagOffset + i * 2 + 1];
                if (valIdx < 0 || valIdx >= Values.Length) continue;
                found = true;
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

        /// <summary>Pops the top value; returns false (leaving <paramref name="value"/> default) if the
        /// stack is empty, which the caller maps to <c>StackOverflow</c>.</summary>
        private static bool Pop(ref FixedList128Bytes<NativeValue> stack, out NativeValue value)
        {
            if (stack.Length == 0) { value = default; return false; }
            value = stack[stack.Length - 1];
            stack.Length -= 1;
            return true;
        }
    }
}
