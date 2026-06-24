using System.Runtime.CompilerServices;

// Grant the Unity EditMode test assembly access to internal members of MapRenderer.Core.
// The Tools/core-tests project compiles Core sources directly into its own assembly, so
// internal members are visible there without this attribute.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
