// Polyfill for C# 9 `init` setters: Unity's reference assemblies lack IsExternalInit (CS0518). Each
// assembly that defines init-only members needs its own copy.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
