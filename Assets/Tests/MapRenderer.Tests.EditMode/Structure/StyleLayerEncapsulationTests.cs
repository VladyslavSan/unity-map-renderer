// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// Two teeth on the style model's <c>JsonValue</c> surface: <see cref="StyleLayer.Raw"/> must stay
    /// public (the restyle survivor gate lives outside <c>MapRenderer.Core</c> and must compare the whole
    /// raw layer object, including unknown/forward-compat keys the typed <c>Paint</c>/<c>Layout</c> views
    /// drop), and every <c>JsonValue</c> field/property anywhere in the style model must be named
    /// <c>Raw</c> or <c>Root</c> (one temporary exception, tracked by UMR-240).
    /// </summary>
    [TestFixture]
    public class StyleLayerEncapsulationTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AnyDeclared = AnyInstance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        [Test]
        public void Raw_IsPublic()
        {
            var fi = typeof(StyleLayer).GetField("Raw", AnyInstance);
            Assert.IsNotNull(fi, "Raw field must exist on StyleLayer.");
            Assert.IsTrue(fi.IsPublic,
                "Raw must be public (SurvivingLayerGate compares it from outside Core).");
        }

        /// <summary>
        /// Every <c>JsonValue</c> field/property under <c>MapRenderer.Unity.Style</c> is named <c>Raw</c> or
        /// <c>Root</c>, with one temporary exception, <see cref="SourceDefinition.Data"/> (tracked by
        /// UMR-240). A member named anything else is a parse artifact escaping the parser under a name
        /// that hides what it really is — <c>StyleLayer.Filter</c> was exactly this, before it became a
        /// typed <see cref="LayerFilter"/>.
        /// </summary>
        [Test]
        public void EveryJsonValueMemberInTheStyleNamespace_IsNamedRawOrRoot()
        {
            var offenders = new List<string>();
            int jsonValueMembers = 0;

            foreach (Type t in typeof(StyleLayer).Assembly.GetTypes())
            {
                if (t.Namespace == null || !t.Namespace.StartsWith("MapRenderer.Unity.Style", StringComparison.Ordinal))
                    continue;
                if (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                    continue; // closures/iterator state machines, not a style-model type

                foreach (FieldInfo f in t.GetFields(AnyDeclared))
                {
                    if (f.FieldType != typeof(JsonValue)) continue;
                    if (f.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) continue; // auto-prop backing field
                    jsonValueMembers++;
                    if (!IsAllowedName(t, f.Name)) offenders.Add($"{t.FullName}.{f.Name}");
                }

                foreach (PropertyInfo p in t.GetProperties(AnyDeclared))
                {
                    if (p.PropertyType != typeof(JsonValue)) continue;
                    jsonValueMembers++;
                    if (!IsAllowedName(t, p.Name)) offenders.Add($"{t.FullName}.{p.Name}");
                }
            }

            Assert.That(jsonValueMembers, Is.GreaterThanOrEqualTo(4),
                "precondition: the scan must find a real corpus of JsonValue members, or 'no offenders' " +
                "means nothing");

            Assert.IsEmpty(offenders,
                "every JsonValue field/property in the style model must be named Raw or Root " +
                "(SourceDefinition.Data is a temporary exception tracked by UMR-240). " +
                $"Offenders: {string.Join(", ", offenders)}");
        }

        private static bool IsAllowedName(Type t, string name)
            => name == "Raw" || name == "Root" || (t == typeof(SourceDefinition) && name == "Data"); // UMR-240 types it
    }
}
