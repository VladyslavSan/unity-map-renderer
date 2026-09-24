using System.Runtime.CompilerServices;

// Shared test-support assembly for the engine-touching helpers EditMode, PlayMode and Visual need.
// MapRenderer.{Core,Jobs,Unity} grant it their internals in their own InternalsVisibleTo files.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.Visual")]
