// Unity EditMode only (not in Tools/core-tests) — the scene every Visual test starts from.

using NUnit.Framework;
using UnityEditor.SceneManagement;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Gives every Visual test the same starting scene: empty, with no camera and no directional light.
    ///
    /// <para>Non-obvious why: the batch runner opens an untitled scene with a "Directional Light", and
    /// <c>SceneIntegrityTests</c> discards it when it opens the project scenes. A Visual test then saw that light
    /// alone and none in the full suite, so tests that add their own light passed in one context and failed in the
    /// other. A single reset before the assembly makes both contexts the same.</para>
    /// </summary>
    [SetUpFixture]
    public sealed class VisualSuiteBaseline
    {
        [OneTimeSetUp]
        public void StartFromAnEmptyScene()
            => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }
}
