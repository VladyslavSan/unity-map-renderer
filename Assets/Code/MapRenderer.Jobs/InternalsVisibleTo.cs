using System.Runtime.CompilerServices;

// Grant the Unity EditMode test assembly access to internal members of MapRenderer.Jobs.
// The Tools/core-tests project compiles these sources directly into its own assembly, so
// internal members are visible there without this attribute.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Shared")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Visual")]
