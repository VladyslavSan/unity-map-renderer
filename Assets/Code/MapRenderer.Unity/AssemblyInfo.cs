using System.Runtime.CompilerServices;

// Lets the EditMode test assembly reach MapRenderer.Unity internals (e.g. MapView.Layers,
// RenderLayerSet) so test-only accessors can live as extension methods in the test assembly
// instead of bloating the production public API. See MapViewTestExtensions.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
