// Engine-free: no UnityEngine, no device/input types.
// Unity.Mathematics + System.Collections.Generic only.

using Unity.Mathematics;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Core-defined touch phase (mirrors the lifecycle of a single finger contact).
    /// NOT <c>UnityEngine.InputSystem.TouchPhase</c> — this enum is defined here so
    /// <see cref="TouchGestureRecognizer"/> and its tests remain engine-free.
    /// </summary>
    public enum TouchPhase
    {
        /// <summary>The finger made contact this frame.</summary>
        Began,

        /// <summary>The finger moved or is held (stationary).</summary>
        Moved,

        /// <summary>The finger lifted (or was cancelled) this frame.</summary>
        Ended,
    }

    /// <summary>
    /// A single finger-contact sample for one frame. Passed as a <c>List&lt;TouchSample&gt;</c>
    /// to <see cref="TouchGestureRecognizer.Recognize"/>. Engine-free; only
    /// <c>Unity.Mathematics</c> types.
    /// </summary>
    public readonly struct TouchSample
    {
        /// <summary>Stable identifier for this finger across frames (from the device layer).</summary>
        public int FingerId { get; init; }

        /// <summary>Screen position in device pixels (+x right, +y up, origin bottom-left).</summary>
        public double2 PositionPx { get; init; }

        /// <summary>Lifecycle phase of the contact this frame.</summary>
        public TouchPhase Phase { get; init; }
    }
}
