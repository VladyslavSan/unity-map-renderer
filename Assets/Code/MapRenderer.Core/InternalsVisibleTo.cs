using System.Runtime.CompilerServices;

// Grant the Unity test assemblies access to MapRenderer.Core internals. Tools/core-tests
// compiles Core sources into its own assembly, so it needs no grant.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Shared")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Visual")]
