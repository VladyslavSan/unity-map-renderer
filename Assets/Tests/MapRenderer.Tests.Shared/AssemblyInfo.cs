using System.Runtime.CompilerServices;

// Shared test-support assembly (cross-platform: includePlatforms empty) holding the engine-touching
// helpers both the EditMode and the PlayMode test assemblies need. Its internal helpers are reached
// from both test assemblies via these grants; the production-internals it touches are granted to THIS
// assembly by MapRenderer.{Core,Jobs,Unity}'s own InternalsVisibleTo files.
[assembly: InternalsVisibleTo("MapRenderer.Tests.EditMode")]
[assembly: InternalsVisibleTo("MapRenderer.Tests.PlayMode")]
