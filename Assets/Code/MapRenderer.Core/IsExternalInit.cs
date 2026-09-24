// Polyfill: C# 9 `init` setters need IsExternalInit, which Unity 6000.5's netstandard reference assemblies
// do not ship (CS0518). Each assembly that defines init-only members needs its own copy.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
