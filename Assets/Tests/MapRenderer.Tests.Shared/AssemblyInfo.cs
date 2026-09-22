using System.Runtime.CompilerServices;

// Shared test-support assembly (cross-platform: includePlatforms empty) for the engine-touching helpers
// EditMode, PlayMode and Visual need. MapRenderer.{Core,Jobs,Unity} grant their own internals to this
// assembly through their InternalsVisibleTo files.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Visual")]
