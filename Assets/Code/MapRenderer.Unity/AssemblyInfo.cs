using System.Runtime.CompilerServices;

// Lets the test assemblies reach MapRenderer.Unity internals (e.g. MapView.Layers, RenderLayerSet), so
// test-only accessors live as extension methods in a test assembly, not on the production public API.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Shared")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Visual")]

// MapRenderer.App (composition root, input, dev UI) is a first-party consumer of library internals
// (MapView.View/SymbolPlacementSystem, telemetry snapshots) that has no hardened public API to use.
[assembly: InternalsVisibleTo("MapRenderer.App")]
