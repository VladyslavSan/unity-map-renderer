using System.Runtime.CompilerServices;

// The test assemblies reach MapRenderer.App internals (e.g. CameraPresetStore, MapHost.EnsureDirectionalLight,
// MapTelemetryPanel.Pull) — same test-seam mechanism MapRenderer.Unity uses.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Shared")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
