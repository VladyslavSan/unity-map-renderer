namespace MapRenderer.Jobs.Expressions
{
    /// <summary>The opcode set the native filter VM understands. <see cref="NativeFilterCompiler"/> emits
    /// only these; a filter needing anything else stays on the managed path.</summary>
    internal enum NativeOperation : byte
    {
        LiteralNumber,
        LiteralBoolean,
        LiteralString,
        Get,
        Has,
        GeometryEqual,
        Equal,
        Compare,
        Not,
        AllStep,
        PushTrue,
        InStringSet
    }

    /// <summary>
    /// One compiled instruction: an opcode plus two opcode-dependent payload fields, written by
    /// <see cref="NativeFilterCompiler"/> and read by <c>NativeFilterEvaluationJob</c>. Non-local invariant:
    /// <c>InStringSet</c> reads its labels from <c>Binding[Operand..Operand+Immediate)</c>, so the compiler
    /// emits a match's labels into consecutive <see cref="NativeFilterProgram.LiteralStrings"/> slots.
    /// </summary>
    internal readonly struct NativeFilterOperation
    {
        /// <summary>The opcode.</summary>
        internal readonly NativeOperation Operation;

        /// <summary>Integer payload; its meaning depends on <c>Operation</c>.</summary>
        internal readonly int Operand;

        /// <summary>Floating-point payload; its meaning depends on <c>Operation</c>.</summary>
        internal readonly double Immediate;

        internal NativeFilterOperation(NativeOperation operation, int operand = 0, double immediate = 0.0)
        {
            Operation = operation;
            Operand = operand;
            Immediate = immediate;
        }
    }
}
