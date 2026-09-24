// C# 9 `init` setters need IsExternalInit, which Unity's netstandard references do not ship (CS0518).
// Each assembly that defines init-only members needs its own copy of this internal polyfill.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
