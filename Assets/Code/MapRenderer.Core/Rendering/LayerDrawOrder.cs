using System;

namespace MapRenderer.Core.Rendering
{
    /// <summary>
    /// Painter's-algorithm draw-order assignment for coplanar flat layers (fills + lines).
    ///
    /// The style is an ordered list of layers composited bottom-to-top (MapLibre painter's algorithm).
    /// Most map layers are coplanar on the ground plane, so Unity's automatic sort
    /// (render queue → camera distance → depth buffer) z-fights and reorders them wrongly.
    /// Rule (ARCHITECTURE §2 "Layer ordering &amp; draw submission"): <b>we own draw order; we never
    /// rely on Unity's automatic sort.</b>
    ///
    /// This is the <b>interim integer-queue mechanism</b> named in ARCHITECTURE §2:
    /// <c>renderQueue = base + layerIndex</c>, with ZWrite off, all layers
    /// inside ONE transparent queue band. The BatchRendererGroup / custom URP ScriptableRenderPass
    /// target (which scales past the integer-queue trick) is a deferred follow-up.
    ///
    /// Why a single transparent band for BOTH fills and lines (the keystone constraint):
    ///   • Unity draws the entire opaque queue (&lt;2500) before the entire transparent queue (≥2501),
    ///     regardless of the per-material queue offset. If fills stayed opaque and lines transparent,
    ///     a fill declared ABOVE a line would still render UNDER it. MapLibre interleaves fill/line
    ///     freely, so the ordering mechanism must keep them in one band where the per-index offset
    ///     alone decides order.
    ///   • URP excludes Queue≥2501 from the opaque depth/GBuffer prepasses and draws those materials
    ///     via UniversalForward in the transparent phase. Within the transparent phase Unity's only
    ///     remaining tiebreak is camera distance — which on a coplanar plane can only reorder tiles
    ///     WITHIN one layer (disjoint regions; explicitly irrelevant per S07), never across layers,
    ///     because each declared layer gets a DISTINCT queue value.
    ///
    /// Engine-free: this is pure C# (no UnityEngine.Rendering reference). The caller passes the base
    /// queue as an int (e.g. UnityEngine.Rendering.RenderQueue.Transparent = 3000) and assigns the
    /// returned values to <c>material.renderQueue</c>.
    /// </summary>
    public static class LayerDrawOrder
    {
        /// <summary>
        /// Unity's "Transparent" render queue value (<c>UnityEngine.Rendering.RenderQueue.Transparent</c>).
        /// Restated here as a plain int so Core stays engine-free. This is the default base for the band.
        /// </summary>
        public const int TransparentQueue = 3000;

        /// <summary>
        /// First queue value of Unity's transparent band. Materials with <c>renderQueue &gt;= 2501</c> are
        /// drawn in the transparent phase. All painter's-algorithm flat layers must live at or above this.
        /// </summary>
        public const int TransparentBandStart = 2501;

        /// <summary>
        /// Compute the per-layer render-queue values for <paramref name="layerCount"/> declared layers,
        /// using the default <see cref="TransparentQueue"/> base.
        /// </summary>
        public static int[] ComputeQueues(int layerCount) => ComputeQueues(layerCount, TransparentQueue);

        /// <summary>
        /// Compute the per-layer render-queue values for <paramref name="layerCount"/> declared layers.
        ///
        /// Result <c>queues[i] == baseQueue + i</c> — strictly monotonic increasing with declared index,
        /// all distinct, so layer <c>i+1</c> always draws AFTER (on top of) layer <c>i</c>. Index 0 is the
        /// bottom-most declared layer; the highest index is drawn last (painter's algorithm).
        /// </summary>
        /// <param name="layerCount">Number of declared layers (&gt;= 0).</param>
        /// <param name="baseQueue">
        /// Render queue of the bottom-most layer (index 0). Must place the whole band inside Unity's
        /// transparent range so ZWrite-off painter's ordering is honoured:
        /// <c>baseQueue &gt;= <see cref="TransparentBandStart"/></c>.
        /// </param>
        /// <returns>An <c>int[layerCount]</c> of render-queue values, one per declared layer index.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// If <paramref name="layerCount"/> is negative, or <paramref name="baseQueue"/> is below the
        /// transparent band start, or <c>baseQueue + (layerCount - 1)</c> would overflow Unity's queue
        /// upper bound (5000).
        /// </exception>
        public static int[] ComputeQueues(int layerCount, int baseQueue)
        {
            if (layerCount < 0)
                throw new ArgumentOutOfRangeException(nameof(layerCount),
                    layerCount, "layerCount must be non-negative.");
            if (baseQueue < TransparentBandStart)
                throw new ArgumentOutOfRangeException(nameof(baseQueue), baseQueue,
                    $"baseQueue must be in the transparent band (>= {TransparentBandStart}) so the " +
                    "painter's-algorithm ZWrite-off ordering is not pre-empted by the opaque phase.");

            // Unity render queues are clamped to [0, 5000]; refuse to assign a value that would
            // exceed the ceiling rather than silently saturate (which would collapse two layers
            // onto the same queue and break strict ordering).
            const int queueCeiling = 5000;
            if (layerCount > 0 && (long)baseQueue + (layerCount - 1) > queueCeiling)
                throw new ArgumentOutOfRangeException(nameof(layerCount), layerCount,
                    $"baseQueue ({baseQueue}) + {layerCount} layers exceeds Unity's render-queue " +
                    $"ceiling ({queueCeiling}). The integer-queue interim cannot represent this many " +
                    "layers; use the BatchRendererGroup target.");

            var queues = new int[layerCount];
            for (int i = 0; i < layerCount; i++)
                queues[i] = baseQueue + i;
            return queues;
        }
    }
}
