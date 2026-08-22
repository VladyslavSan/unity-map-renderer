using System.Runtime.CompilerServices;

// Lets the EditMode test assembly reach MapRenderer.Unity internals (e.g. MapView.Layers,
// RenderLayerSet) so test-only accessors can live as extension methods in the test assembly
// instead of bloating the production public API. See MapViewTestExtensions (now in the shared
// test-support assembly, granted below).
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Shared")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]

// The app/demo layer (MapRenderer.App — composition root, input, dev UI) is a first-party consumer that
// reaches library internals (MapView.View/Labels, telemetry snapshots, the label-breakdown trigger). Same
// mechanism as the test assemblies above: physical separation without a hardened public API surface yet.
[assembly: InternalsVisibleTo("MapRenderer.App")]
