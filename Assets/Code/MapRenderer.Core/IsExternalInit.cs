// C# 9 `init`-only setters require System.Runtime.CompilerServices.IsExternalInit, which Unity 6000.5's
// netstandard reference assemblies do NOT ship (compile fails with CS0518). This internal polyfill supplies
// it for this assembly. One copy is needed per assembly that DEFINES init-only members.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
