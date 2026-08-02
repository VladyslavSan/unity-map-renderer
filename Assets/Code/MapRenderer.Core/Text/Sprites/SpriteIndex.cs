// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// A parsed MapLibre sprite JSON index: name → <see cref="SpriteEntry"/>. Mirrors
    /// <c>StyleParser</c>'s forward-compat posture — malformed or structurally-wrong input never
    /// throws, it just yields an empty (or partial) index.
    /// </summary>
    public sealed class SpriteIndex
    {
        private readonly Dictionary<string, SpriteEntry> _entries = new Dictionary<string, SpriteEntry>();

        /// <summary>Number of sprites in the index.</summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Read-only enumeration of every name → entry pair — the same class of access as
        /// <see cref="TryGetSprite"/>, just unkeyed. Consumer: <c>SpriteSheetPadder.Plan</c>, which must walk
        /// the whole index to repack it.
        /// </summary>
        public IReadOnlyDictionary<string, SpriteEntry> Entries => _entries;

        /// <summary>
        /// Builds an index from already-derived entries — the repack's output path. <c>internal</c> on
        /// purpose: constructing an index that did NOT come from a sprite JSON is privileged, and only
        /// <c>SpriteSheetPadder.Plan</c> may do it. COPIES <paramref name="entries"/> so the returned index
        /// keeps the "immutable after construction" property <see cref="Parse"/> establishes.
        /// </summary>
        internal static SpriteIndex FromEntries(IReadOnlyDictionary<string, SpriteEntry> entries)
        {
            var index = new SpriteIndex();
            if (entries == null)
                return index;

            foreach (var kv in entries)
                index._entries[kv.Key] = kv.Value;

            return index;
        }

        /// <summary>Parses a sprite JSON document's text. Malformed JSON yields an empty index (never throws).</summary>
        public static SpriteIndex Parse(string json)
        {
            if (json == null)
                return new SpriteIndex();

            try
            {
                return Parse(JsonParser.Parse(json));
            }
            catch (JsonParseException)
            {
                return new SpriteIndex();
            }
        }

        /// <summary>Parses an already-parsed sprite JSON root. A non-object root yields an empty index.</summary>
        public static SpriteIndex Parse(JsonValue root)
        {
            var index = new SpriteIndex();
            if (root == null || !root.IsObject)
                return index;

            foreach (var kv in root.Members)
            {
                if (!kv.Value.IsObject) continue; // skip malformed member entries (forward-compat)

                var v = kv.Value;
                index._entries[kv.Key] = new SpriteEntry
                {
                    X = v.GetInt("x", 0),
                    Y = v.GetInt("y", 0),
                    Width = v.GetInt("width", 0),
                    Height = v.GetInt("height", 0),
                    PixelRatio = (float)v.GetDouble("pixelRatio", 1.0),
                    Sdf = v.TryGet("sdf", out var s) && s.AsBool(false)
                };
            }

            return index;
        }

        /// <summary>Looks up a sprite by name. False + default when absent.</summary>
        public bool TryGetSprite(string name, out SpriteEntry entry)
        {
            if (name != null && _entries.TryGetValue(name, out entry))
                return true;
            entry = default;
            return false;
        }
    }
}
