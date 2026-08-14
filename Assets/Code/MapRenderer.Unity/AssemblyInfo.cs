using System.Runtime.CompilerServices;

// Lets the EditMode test assembly reach MapRenderer.Unity internals (e.g. MapView.Layers,
// RenderLayerSet) so test-only accessors can live as extension methods in the test assembly
// instead of bloating the production public API. See MapViewTestExtensions (now in the shared
// test-support assembly, granted below).
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Shared")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
