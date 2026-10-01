using System;
using System.Collections.Generic;
using UnityEngine.Rendering;
using MapRenderer.Core.Lifetime;

namespace MapRenderer.Unity.Rendering.Backend
{
    /// <summary>One registered draw item, as the shared visibility model sees it: its layer slot and its own hidden flag.</summary>
    internal interface IDrawItem
    {
        /// <summary>The layer's global SLOT.</summary>
        int MaterialIndex { get; }

        /// <summary>The item's own flag (<see cref="ITileRenderBackend.SetItemsVisible"/>).
        /// The item draws only when it is not hidden and its slot is visible.</summary>
        bool Hidden { get; set; }
    }

    /// <summary>
    /// The visibility model every tile-draw backend shares: the per-slot draw gate, the per-slot shadow list, each item's
    /// hidden flag, and the rule that an item draws only when it is not hidden and its slot is visible. A backend implements
    /// only how a change reaches its own draw path. The two apply hooks are abstract on purpose, so a backend with nothing to
    /// do says so with an empty body; <see cref="EndItemVisibilityBatch"/> is virtual. A slot never declared is visible, and a
    /// slot with no shadow mode is <see cref="ShadowCastingMode.Off"/>.
    /// </summary>
    /// <typeparam name="TItem">The backend's own item record.</typeparam>
    internal abstract class TileRenderBackendBase<TItem> : VerifiedDisposable where TItem : struct, IDrawItem
    {
        /// <summary>Handle → item. Internal so the test assembly's per-backend extensions can read it.</summary>
        internal readonly Dictionary<int, TItem> _items = new Dictionary<int, TItem>(64);

        /// <summary>Per-slot shadow-cast declaration (<c>IRenderLayer.CastShadows</c>), carried verbatim from the caller.</summary>
        private readonly List<ShadowCastingMode> _layerShadowModes = new List<ShadowCastingMode>();

        /// <summary>Per-slot draw gate (<see cref="SetLayerVisible"/>). A slot past the end of the list is visible.</summary>
        private readonly List<bool> _layerVisible = new List<bool>();

        /// <param name="layerShadowModes">Optional per-slot shadow modes; null or short means
        /// <see cref="ShadowCastingMode.Off"/>.</param>
        protected TileRenderBackendBase(IReadOnlyList<ShadowCastingMode> layerShadowModes)
        {
            if (layerShadowModes != null)
                for (int i = 0; i < layerShadowModes.Count; i++) _layerShadowModes.Add(layerShadowModes[i]);
        }

        /// <summary>The declared shadow mode of <paramref name="slot"/>, or <see cref="ShadowCastingMode.Off"/> when no list
        /// was supplied or it is short.</summary>
        protected ShadowCastingMode ShadowModeFor(int slot)
            => (uint)slot < (uint)_layerShadowModes.Count ? _layerShadowModes[slot] : ShadowCastingMode.Off;

        /// <summary>True when <paramref name="slot"/> passes its draw gate.</summary>
        protected bool Visible(int slot)
            => (uint)slot >= (uint)_layerVisible.Count || _layerVisible[slot];

        /// <summary>Restyle: a slot past the new width loses its gate, and the shadow list is replaced.</summary>
        /// <param name="slotCount">The new full-width slot count.</param>
        /// <param name="layerShadowModes">The new per-slot shadow modes, or null.</param>
        protected void ReplaceSlotLists(int slotCount, IReadOnlyList<ShadowCastingMode> layerShadowModes)
        {
            if (_layerVisible.Count > slotCount)
                _layerVisible.RemoveRange(slotCount, _layerVisible.Count - slotCount);

            _layerShadowModes.Clear();
            if (layerShadowModes != null)
                for (int i = 0; i < layerShadowModes.Count; i++) _layerShadowModes.Add(layerShadowModes[i]);
        }

        /// <inheritdoc cref="ITileRenderBackend.SetLayerVisible"/>
        public void SetLayerVisible(int slot, bool visible)
        {
            if (IsDisposed || slot < 0) return;
            while (_layerVisible.Count <= slot) _layerVisible.Add(true);
            if (_layerVisible[slot] == visible) return; // unchanged ⇒ nothing to apply
            _layerVisible[slot] = visible;
            ApplySlotGate(slot, visible);
        }

        /// <inheritdoc cref="ITileRenderBackend.SetItemsVisible"/>
        public void SetItemsVisible(ReadOnlySpan<int> handles, bool visible)
        {
            if (IsDisposed) return;
            for (int i = 0; i < handles.Length; i++)
            {
                if (!_items.TryGetValue(handles[i], out TItem item) || item.Hidden == !visible) continue;
                item.Hidden = !visible;
                _items[handles[i]] = item;
                ApplyItemVisibility(item, visible, Visible(item.MaterialIndex));
            }

            EndItemVisibilityBatch(visible);
        }

        /// <summary>Moves every not-hidden item of <paramref name="slot"/> to the gate's new state.</summary>
        protected abstract void ApplySlotGate(int slot, bool visible);

        /// <summary>Moves one item whose own flag changed. It draws only when <paramref name="visible"/> and
        /// <paramref name="slotVisible"/> (its slot passes the gate) are both true.</summary>
        protected abstract void ApplyItemVisibility(TItem item, bool visible, bool slotVisible);

        /// <summary>Called once after a <see cref="SetItemsVisible"/> batch, for a backend that gathers its changes into
        /// one operation.</summary>
        protected virtual void EndItemVisibilityBatch(bool visible) { }
    }
}
