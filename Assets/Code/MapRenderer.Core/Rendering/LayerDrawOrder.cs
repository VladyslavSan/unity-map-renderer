using System;

namespace MapRenderer.Core.Rendering
{
    /// <summary>
    /// The ordering role of a material WITHIN one declared layer's queue band, not across layers.
    /// <see cref="Above"/> draws over <see cref="Base"/> of the same layer and below the NEXT layer's band.
    /// A symbol layer maps icon = <see cref="Base"/> and text = <see cref="Above"/>; other kinds use
    /// <see cref="Base"/> only. Not <c>Overlay</c>: that names Unity's <c>RenderQueue.Overlay</c>. Values
    /// are contiguous queue offsets from 0; <see cref="LayerDrawOrder.SubSlotsPerLayer"/> equals their count.
    /// </summary>
    public enum LayerSubSlot { Base = 0, Above = 1 }

    /// <summary>
    /// Painter's-algorithm draw order for coplanar flat layers, because Unity's automatic sort z-fights them
    /// (<c>ARCHITECTURE.md</c> § "Layer ordering &amp; draw submission"). Each layer owns a band of
    /// <see cref="SubSlotsPerLayer"/> queue values (<c>base + layerIndex * SubSlotsPerLayer + subSlot</c>),
    /// ZWrite off, for the caller's <c>material.renderQueue</c>. Non-local invariant: fills and lines share
    /// ONE transparent band, because Unity draws all opaque queues before all transparent ones; camera
    /// distance then reorders only tiles within a layer, never layers with distinct queues.
    /// </summary>
    public static class LayerDrawOrder
    {
        /// <summary>
        /// Number of sub-slots reserved per declared layer — i.e. the queue stride between one layer's
        /// <see cref="LayerSubSlot.Base"/> and the next layer's <see cref="LayerSubSlot.Base"/>. Also equals
        /// <c>Enum.GetValues(typeof(LayerSubSlot)).Length</c> (pinned by a test). Adding a future
        /// sub-slot means adding a <see cref="LayerSubSlot"/> value AND bumping this constant; nothing else
        /// re-derives the stride.
        /// </summary>
        public const int SubSlotsPerLayer = 2;

        /// <summary>
        /// Unity's clamp bound for <c>Material.renderQueue</c> — values above this saturate rather than
        /// order correctly, which would silently collapse two sub-slots (or two layers) onto one queue.
        /// <see cref="QueueFor"/> validates against it rather than trust the caller.
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
        /// Render queue for one declared layer's sub-slot: the one home of the formula
        /// <c>TransparentQueue + drawIndex * SubSlotsPerLayer + subSlot</c>. Every caller derives its queue
        /// from this, one layer at a time.
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

            // A cast can produce an undeclared sub-slot, which lands in the NEXT layer's band and breaks disjoint
            // bands: `QueueFor(i, (LayerSubSlot)SubSlotsPerLayer)` == `QueueFor(i + 1, Base)`.
            if ((uint)subSlot >= SubSlotsPerLayer)
                throw new ArgumentOutOfRangeException(nameof(subSlot), subSlot,
                    $"sub-slot must be a declared {nameof(LayerSubSlot)} value (0..{SubSlotsPerLayer - 1}); " +
                    "an out-of-band sub-slot would spill into the next layer's queue band.");

            // `long` throughout: in int arithmetic a large drawIndex overflows to negative, slipping past the
            // ceiling check below and returning a NEGATIVE queue.
            long queue = (long)TransparentQueue + (long)drawIndex * SubSlotsPerLayer + (int)subSlot;
            if (queue > QueueCeiling)
                throw new ArgumentOutOfRangeException(nameof(drawIndex), drawIndex,
                    $"drawIndex {drawIndex} sub-slot {subSlot} would need queue {queue}, which exceeds " +
                    $"Unity's render-queue ceiling ({QueueCeiling}). The integer-queue interim cannot " +
                    "represent this many layers; use the BatchRendererGroup target.");
            return (int)queue;
        }
    }
}
