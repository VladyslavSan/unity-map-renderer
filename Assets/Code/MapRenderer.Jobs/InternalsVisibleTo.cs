using System.Runtime.CompilerServices;

// Grant the Unity EditMode test assembly access to internal members of MapRenderer.Jobs.
// The Tools/core-tests project compiles these sources directly into its own assembly, so
// internal members are visible there without this attribute.
// (IR C1: FeatureSelector.FilterFor moved here from MapRenderer.Core, which carries the twin of this file.)
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
