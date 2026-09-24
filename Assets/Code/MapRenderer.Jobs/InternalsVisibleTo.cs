using System.Runtime.CompilerServices;

// Grant the Unity test assemblies access to internal members of MapRenderer.Jobs.
// Tools/core-tests compiles the sources it needs, so it needs no attribute.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Shared")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Visual")]
