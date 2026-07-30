using System;

namespace MapRenderer.Core.Rendering
{
    /// <summary>
    /// The ordering role of a material <b>within one declared layer's own queue band</b> — NOT a
    /// cross-layer concept. <see cref="Above"/> draws on top of <see cref="Base"/> <i>of the same
    /// layer</i>, and still strictly below every sub-slot of the NEXT layer's band. A symbol layer
    /// maps icon = <see cref="Base"/>, text = <see cref="Above"/>, so the badge draws under the number
    /// it frames (G7/D7); every other kind (fill, line, background) uses <see cref="Base"/> only.
    ///
    /// <para>Named <c>Above</c>, not <c>Overlay</c>: <c>Overlay</c> already names Unity's
    /// <c>RenderQueue.Overlay</c> (4000) in this render-queue domain — the very pin the Unity-side
    /// symbol render layer was freed from (E2/D11) — so reusing it here for an unrelated,
    /// per-layer-relative concept would collide.</para>
    ///
    /// <para>Values must stay contiguous from 0 — they are queue offsets, not flags
    /// (<see cref="LayerDrawOrder.SubSlotsPerLayer"/> is pinned to equal the value count).</para>
    /// </summary>
    public enum LayerSubSlot { Base = 0, Above = 1 }

    /// <summary>
    /// Painter's-algorithm draw-order assignment for coplanar flat layers (fills + lines).
    ///
    /// The style is an ordered list of layers composited bottom-to-top (MapLibre painter's algorithm).
    /// Most map layers are coplanar on the ground plane, so Unity's automatic sort
    /// (render queue → camera distance → depth buffer) z-fights and reorders them wrongly.
    /// Rule (ARCHITECTURE §2 "Layer ordering &amp; draw submission"): <b>we own draw order; we never
    /// rely on Unity's automatic sort.</b>
    ///
    /// This is the <b>interim integer-queue mechanism</b> named in ARCHITECTURE §2: each declared layer
    /// owns a contiguous <b>band</b> of <see cref="SubSlotsPerLayer"/> queue values
    /// (<c>base + layerIndex * SubSlotsPerLayer + subSlot</c>), with ZWrite off, all layers inside ONE
    /// transparent queue band. A symbol layer's icon sits at <see cref="LayerSubSlot.Base"/> and its text at
    /// <see cref="LayerSubSlot.Above"/> so the badge never paints over its own number (G7/D7, Stage 2 of
    /// road-shields) — every other kind uses only <see cref="LayerSubSlot.Base"/>. The BatchRendererGroup /
    /// custom URP ScriptableRenderPass target (which scales past the integer-queue trick) is a deferred
    /// follow-up.
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
        /// Number of sub-slots reserved per declared layer — i.e. the queue stride between one layer's
        /// <see cref="LayerSubSlot.Base"/> and the next layer's <see cref="LayerSubSlot.Base"/>. Also equals
        /// <c>Enum.GetValues(typeof(LayerSubSlot)).Length</c> (pinned by a test — N4). Adding a future
        /// sub-slot means adding a <see cref="LayerSubSlot"/> value AND bumping this constant; nothing else
        /// re-derives the stride.
        /// </summary>
        public const int SubSlotsPerLayer = 2;

        /// <summary>
        /// Unity's clamp bound for <c>Material.renderQueue</c> — values above this saturate rather than
        /// order correctly, which would silently collapse two sub-slots (or two layers) onto one queue.
        /// <see cref="QueueFor"/> and <see cref="ComputeQueues(int, int)"/> both validate against it rather
        /// than trust the caller.
        /// </summary>
        public const int QueueCeiling = 5000;

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
        /// Render queue for a single declared layer's sub-slot — the one formula home for
        /// <c>TransparentQueue + drawIndex * SubSlotsPerLayer + subSlot</c>. Callers assigning a single
        /// layer's queue (rather than a whole-style batch via <see cref="ComputeQueues(int)"/>) use this
        /// instead of re-deriving the offset inline. <paramref name="subSlot"/> defaults to
        /// <see cref="LayerSubSlot.Base"/>, so every existing single-sub-slot call site stays
        /// source-compatible.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// If <paramref name="drawIndex"/> is negative, or the resulting queue would exceed
        /// <see cref="QueueCeiling"/>.
        /// </exception>
        public static int QueueFor(int drawIndex, LayerSubSlot subSlot = LayerSubSlot.Base)
        {
            if (drawIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(drawIndex),
                    drawIndex, "drawIndex must be non-negative.");

            // A cast can produce a value the enum never declares; an undeclared sub-slot would land in the
            // NEXT layer's band (`QueueFor(i, (LayerSubSlot)SubSlotsPerLayer)` == `QueueFor(i + 1, Base)`),
            // silently breaking the disjoint-bands invariant this type exists to guarantee.
            if ((uint)subSlot >= SubSlotsPerLayer)
                throw new ArgumentOutOfRangeException(nameof(subSlot), subSlot,
                    $"sub-slot must be a declared {nameof(LayerSubSlot)} value (0..{SubSlotsPerLayer - 1}); " +
                    "an out-of-band sub-slot would spill into the next layer's queue band.");

            // `long` throughout: `drawIndex * SubSlotsPerLayer` in int arithmetic overflows to negative for a
            // large drawIndex, which would slip past the ceiling check below and return a NEGATIVE queue —
            // exactly what this guard exists to prevent. Mirrors ComputeQueues' own widening.
            long queue = (long)TransparentQueue + (long)drawIndex * SubSlotsPerLayer + (int)subSlot;
            if (queue > QueueCeiling)
                throw new ArgumentOutOfRangeException(nameof(drawIndex), drawIndex,
                    $"drawIndex {drawIndex} sub-slot {subSlot} would need queue {queue}, which exceeds " +
                    $"Unity's render-queue ceiling ({QueueCeiling}). The integer-queue interim cannot " +
                    "represent this many layers; use the BatchRendererGroup target.");
            return (int)queue;
        }

        /// <summary>
        /// Compute the per-layer render-queue values for <paramref name="layerCount"/> declared layers,
        /// using the default <see cref="TransparentQueue"/> base.
        /// </summary>
        public static int[] ComputeQueues(int layerCount) => ComputeQueues(layerCount, TransparentQueue);

        /// <summary>
        /// Compute the per-layer render-queue values for <paramref name="layerCount"/> declared layers —
        /// the <see cref="LayerSubSlot.Base"/> sub-slot of each (the batch form of <see cref="QueueFor"/>,
        /// pinned no-drift against it — N3).
        ///
        /// Result <c>queues[i] == QueueFor(i, LayerSubSlot.Base) == baseQueue + i * SubSlotsPerLayer</c> —
        /// strictly monotonic increasing with declared index, all distinct, so layer <c>i+1</c>'s whole band
        /// always draws AFTER (on top of) layer <c>i</c>'s whole band. Index 0 is the bottom-most declared
        /// layer; the highest index is drawn last (painter's algorithm).
        /// </summary>
        /// <param name="layerCount">Number of declared layers (&gt;= 0).</param>
        /// <param name="baseQueue">
        /// Render queue of the bottom-most layer's <see cref="LayerSubSlot.Base"/> sub-slot (index 0). Must
        /// place the whole band inside Unity's transparent range so ZWrite-off painter's ordering is
        /// honoured: <c>baseQueue &gt;= <see cref="TransparentBandStart"/></c>.
        /// </param>
        /// <returns>An <c>int[layerCount]</c> of render-queue values, one per declared layer index.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// If <paramref name="layerCount"/> is negative, or <paramref name="baseQueue"/> is below the
        /// transparent band start, or the LAST layer's <see cref="LayerSubSlot.Above"/> sub-slot — the
        /// band's top, not just its <see cref="LayerSubSlot.Base"/> — would overflow Unity's queue upper
        /// bound (<see cref="QueueCeiling"/>).
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
            // exceed the ceiling rather than silently saturate (which would collapse two sub-slots, or
            // two layers, onto the same queue and break strict ordering). The check must cover the BAND'S
            // TOP — the last layer's Above sub-slot — not just its Base: at base 3000, SubSlotsPerLayer=2,
            // 1000 layers puts the last layer's Above slot at 3000 + 999*2 + 1 = 4999 (OK); 1001 would need
            // 3000 + 1000*2 + 1 = 5001 (throws). Getting this wrong the obvious way
            // (checking only baseQueue + (layerCount-1)*SubSlotsPerLayer, the Base slot) would let the last
            // layer's Above slot silently saturate — exactly the collapse this guard exists to prevent.
            if (layerCount > 0 &&
                (long)baseQueue + (long)layerCount * SubSlotsPerLayer - 1 > QueueCeiling)
                throw new ArgumentOutOfRangeException(nameof(layerCount), layerCount,
                    $"baseQueue ({baseQueue}) + {layerCount} layers (top sub-slot) exceeds Unity's " +
                    $"render-queue ceiling ({QueueCeiling}). The integer-queue interim cannot represent " +
                    "this many layers; use the BatchRendererGroup target.");

            var queues = new int[layerCount];
            for (int i = 0; i < layerCount; i++)
                queues[i] = baseQueue + i * SubSlotsPerLayer;
            return queues;
        }
    }
}
