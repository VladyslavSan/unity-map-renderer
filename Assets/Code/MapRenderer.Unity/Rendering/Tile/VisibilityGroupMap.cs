using System;
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Unity.Rendering.Layers;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Style;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// The order a new tile's layers appear in. The configured kinds map each render slot to a visibility group, and each source slot to the
    /// groups its layers fill. Slots never renumber, so only these maps change when the style or the list does. A kind in no group falls in
    /// the last group, a list with no group is one group of every layer, and groups past the 64th join the 64th.
    /// </summary>
    internal sealed class VisibilityGroupMap
    {
        /// <summary>The kinds of each configured group; null means one group holding every layer. A map nobody configured uses the default order.</summary>
        private StyleLayerType[][] _kinds = ToKinds(VisibilityGroup.DefaultOrder());

        private ulong[] _groupsOfSource = Array.Empty<ulong>();

        /// <summary>The number of groups.</summary>
        public int GroupCount { get; private set; } = 1;

        /// <summary>A mask with the bit of every group set.</summary>
        public ulong AllGroups { get; private set; } = 1UL;

        /// <summary>The group of each render slot (-1 for a slot with no tile mesh), for <see cref="RemapMask"/> after a change.</summary>
        public int[] GroupOfSlots { get; private set; } = Array.Empty<int>();

        /// <summary>True iff <paramref name="groups"/> lists the same kinds, group by group, as the configured list.</summary>
        public bool SameAs(IReadOnlyList<VisibilityGroup> groups)
        {
            if (groups == null || groups.Count == 0) return _kinds == null;
            if (_kinds == null || _kinds.Length != groups.Count) return false;
            for (int g = 0; g < groups.Count; g++)
            {
                StyleLayerType[] have = _kinds[g];
                StyleLayerType[] want = groups[g]?.Kinds;
                int wanted = want?.Length ?? 0;
                if (have.Length != wanted) return false;
                for (int k = 0; k < wanted; k++)
                    if (have[k] != want[k]) return false;
            }

            return true;
        }

        /// <summary>Stores a copy of the kinds of <paramref name="groups"/>. A null or empty list is one group of everything. The maps stay as
        /// they were until <see cref="Rebuild"/>.</summary>
        public void SetKinds(IReadOnlyList<VisibilityGroup> groups) => _kinds = ToKinds(groups);

        /// <summary>Resolves every render slot of <paramref name="layers"/> to its group, and sizes the per-source masks to zero.
        /// The caller then fills each source slot with <see cref="SetGroupsOfSource"/>. Non-local invariant: it assigns a NEW slot array,
        /// so a <see cref="GroupOfSlots"/> captured before the call never aliases the new one.</summary>
        public void Rebuild(RenderLayerSet layers, int sourceCount)
        {
            int configured = _kinds?.Length ?? 0;
            GroupCount = configured == 0 ? 1 : math.min(configured, 64); // groups past 64 join the last one
            AllGroups  = GroupCount == 64 ? ulong.MaxValue : (1UL << GroupCount) - 1UL;

            GroupOfSlots = new int[layers.Count];
            for (int li = 0; li < layers.Count; li++)
                GroupOfSlots[li] = layers[li] is ITileMeshRenderLayer || layers[li] is BackgroundRenderLayer
                    ? GroupOfKind(layers[li].StyleLayer?.LayerType ?? StyleLayerType.Unknown)
                    : -1;

            _groupsOfSource = new ulong[sourceCount];
        }

        /// <summary>The groups the layers at <paramref name="layerSlots"/> fill.</summary>
        public ulong GroupsOf(List<int> layerSlots)
        {
            ulong mask = 0;
            for (int i = 0; i < layerSlots.Count; i++) mask |= GroupBit(layerSlots[i]);
            return mask;
        }

        /// <summary>Sets the groups the layers of <paramref name="slot"/>'s source fill.</summary>
        public void SetGroupsOfSource(int slot, ulong groups) => _groupsOfSource[slot] = groups;

        /// <summary>The groups the layers of <paramref name="slot"/>'s source fill. A source with no mesh layer has none.</summary>
        public ulong GroupsOfSource(int slot) => _groupsOfSource[slot];

        /// <summary>The group of the layer at render slot <paramref name="materialIndex"/>; a slot with no tile mesh falls into the last group.</summary>
        private int GroupOfSlot(int materialIndex)
            => (uint)materialIndex < (uint)GroupOfSlots.Length && GroupOfSlots[materialIndex] >= 0 ? GroupOfSlots[materialIndex] : GroupCount - 1;

        /// <summary>The bit of the group of the layer at render slot <paramref name="materialIndex"/>.</summary>
        public ulong GroupBit(int materialIndex) => 1UL << GroupOfSlot(materialIndex);

        /// <summary>Moves a revealed-groups mask from <paramref name="previousGroupOfSlot"/> to the current layout. A mask of every group stays so. Otherwise
        /// a group counts as revealed only when every layer in it was, so nothing shown hides and nothing hidden shows early.</summary>
        public ulong RemapMask(ulong oldGroups, int[] previousGroupOfSlot)
        {
            if (oldGroups == ulong.MaxValue) return ulong.MaxValue;
            ulong shown   = 0;
            ulong unshown = 0;
            for (int slot = 0; slot < GroupOfSlots.Length; slot++)
            {
                bool oldMesh = (uint)slot < (uint)previousGroupOfSlot.Length && previousGroupOfSlot[slot] >= 0;
                if (!oldMesh || GroupOfSlots[slot] < 0) continue; // a slot with no tile mesh shows nothing to remap
                int oldGroup = previousGroupOfSlot[slot];
                if ((oldGroups & (1UL << oldGroup)) != 0) shown |= GroupBit(slot);
                else unshown |= GroupBit(slot);
            }

            return shown & ~unshown;
        }

        private static StyleLayerType[][] ToKinds(IReadOnlyList<VisibilityGroup> groups)
        {
            if (groups == null || groups.Count == 0) return null;
            var kinds = new StyleLayerType[groups.Count][];
            for (int g = 0; g < kinds.Length; g++)
                kinds[g] = groups[g]?.Kinds != null ? (StyleLayerType[])groups[g].Kinds.Clone() : Array.Empty<StyleLayerType>();
            return kinds;
        }

        /// <summary>The group a layer kind appears in: the first group listing it, else the last group.</summary>
        private int GroupOfKind(StyleLayerType kind)
        {
            if (_kinds == null) return 0;
            for (int g = 0; g < _kinds.Length; g++)
            {
                StyleLayerType[] kinds = _kinds[g];
                for (int k = 0; k < kinds.Length; k++)
                    if (kinds[k] == kind) return math.min(g, GroupCount - 1);
            }

            return GroupCount - 1;
        }
    }
}
