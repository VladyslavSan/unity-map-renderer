using System;
using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Unity.Style
{
    /// <summary>
    /// Reads the expression properties of one style block (<c>paint</c>, <c>layout</c>, <c>light</c>,
    /// <c>sky</c>). A property that fails to parse takes its default, and <see cref="Errors"/> records one
    /// message for it. A malformed property therefore never costs the rest of the layer.
    /// </summary>
    internal sealed class PropertyReader
    {
        private readonly string _blockName;
        private readonly List<string> _errors = new List<string>();

        /// <summary>One message per property that fell back to its default. Never null.</summary>
        internal IReadOnlyList<string> Errors => _errors;

        /// <summary>Creates a reader for one block.</summary>
        /// <param name="blockName">The block name that starts every error message, such as <c>paint</c>.</param>
        internal PropertyReader(string blockName)
        {
            _blockName = blockName;
        }

        /// <summary>Returns <paramref name="fallback"/> when <paramref name="key"/> is absent from
        /// <paramref name="block"/> or when <paramref name="parse"/> fails on its value.</summary>
        internal TProp Read<TProp>(JsonValue block, string key, TProp fallback, Func<JsonValue, TProp> parse)
        {
            JsonValue json = block?.Get(key);
            if (json == null) return fallback;
            try
            {
                return parse(json);
            }
            catch (Exception ex) when (ex is ExpressionParseException || ex is ExpressionEvaluationException)
            {
                _errors.Add($"{_blockName} '{key}' is malformed, so its default is used: {ex.Message}");
                return fallback;
            }
        }

        /// <summary>The common case of <see cref="Read{TProp}"/>: a <see cref="StyleProperty{T}"/> over
        /// <paramref name="project"/>, with a constant <paramref name="defaultValue"/> as the fallback.</summary>
        internal StyleProperty<T> ReadProperty<T>(JsonValue block, string key, T defaultValue,
            Func<Value, T> project, bool interpolatable = true)
            => Read(block, key, new StyleProperty<T>(defaultValue),
                json => new StyleProperty<T>(json, defaultValue, project, interpolatable: interpolatable));
    }
}
