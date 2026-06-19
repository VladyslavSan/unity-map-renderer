// MapLitShaderGUI.cs — map-renderer material inspector; partial derivative of URP LitShader.cs
//
// Origin:   Packages/com.unity.render-pipelines.universal/Editor/ShaderGUI/Shaders/LitShader.cs
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
//
// Scope of the derivative: the bodies of DrawSurfaceOptions, DrawSurfaceInputs, and DrawAdvancedOptions
//   replicate URP LitShader's protected override expressions (workflow popup, LitGUI.Inputs +
//   DrawEmissionProperties + DrawTileOffset, and the highlights/reflections null-guarded block). URP exposes
//   no hook to reach those expressions without copying them, so they are reproduced under UCL — the
//   BaseShaderGUI-fallback "URP GUI code is copied" branch of the S35 ticket. The rest of this file (the
//   Map Paint foldout, FindProperties/ValidateMaterial wiring, DrawProperty helpers) is original.

using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;                       // MaterialHeaderScopeList (com.unity.render-pipelines.core editor)
using UnityEditor.Rendering.Universal.ShaderGUI;   // BaseShaderGUI, LitGUI (com.unity.render-pipelines.universal editor)

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// Custom material inspector shared by <c>MapRenderer/Fill</c> and <c>MapRenderer/Line</c> (S35).
    ///
    /// DERIVES from URP's <see cref="BaseShaderGUI"/> rather than the raw <see cref="ShaderGUI"/>, so the
    /// material inspector gains URP's full Lit UI for free — Surface Options (workflow, surface type, blend,
    /// cull, z-write, alpha clip), Surface Inputs (base/normal/metallic-or-specular/occlusion/emission map
    /// slots with tiling/offset), and Advanced — plus URP's keyword / render-state sync via
    /// <see cref="ValidateMaterial"/>. We then add a single "Map Paint" foldout for the MapLibre styling
    /// knobs (_MapColor, _Opacity, and the line knobs when editing the line material).
    ///
    /// DERIVES the structure from <see cref="BaseShaderGUI"/> (<c>public abstract</c>, built to be subclassed
    /// — the hooks <see cref="DrawSurfaceOptions"/>, <see cref="DrawSurfaceInputs"/>,
    /// <see cref="DrawAdvancedOptions"/>, <see cref="FillAdditionalFoldouts"/> exist, and the Lit
    /// surface-inputs + keyword behaviour is reachable through the <c>public</c> <see cref="LitGUI"/> API).
    /// But the three <c>Draw*</c> override BODIES still REPLICATE URP <c>LitShader</c>'s protected override
    /// expressions verbatim — URP exposes no hook to reach them without copying. Those replicated bodies are
    /// attributed under the Unity Companion License (UCL header above + THIRD-PARTY-NOTICES.txt entry,
    /// <c>MapLitShaderGUI.cs ← derived from LitShader.cs</c>). This is the BaseShaderGUI-fallback "URP GUI
    /// code is copied" branch; see docs/lit-rendering-design.md for the sharpened copy-vs-derive rule.
    ///
    /// This mirrors URP's own internal <c>LitShader</c> GUI minus its Details foldout: <c>LitDetailGUI</c> is
    /// <c>internal</c> and cannot be reached from this assembly, so the detail-map foldout is intentionally
    /// dropped — the one scope reduction. All other Lit sections are full-fidelity.
    ///
    /// Note: the inspector layout is a manual in-Editor check — headless batch mode cannot verify UI.
    /// Headless tests only assert the base type and that this assembly compiles against the URP editor asmdef.
    ///
    /// See docs/lit-rendering-design.md for the full ShaderGUI design rationale and the copy-vs-derive rule.
    /// </summary>
    public class MapLitShaderGUI : BaseShaderGUI
    {
        /// <summary>Persisted expand-state bit for the Map Paint foldout. Must not collide with URP's
        /// <c>BaseShaderGUI.Expandable</c> bits (SurfaceOptions=1&lt;&lt;0 … Details=1&lt;&lt;3).</summary>
        private enum MapExpandable
        {
            MapPaint = 1 << 4,
        }

        private static readonly GUIContent s_MapPaintHeader = new GUIContent("Map Paint",
            "MapLibre-style paint properties applied on top of the URP Lit surface.");

        private static readonly GUIContent s_MapColorLabel = new GUIContent("Map Color",
            "MapLibre-style fill/line color. Multiplied onto the albedo (base color × map color). " +
            "Change this property to restyle without a mesh rebuild.");
        private static readonly GUIContent s_OpacityLabel = new GUIContent("Opacity",
            "Overall opacity [0,1]. Multiplied onto alpha.");

        private static readonly GUIContent s_WidthLabel = new GUIContent("Width (m or px)",
            "Line width, interpreted as world meters or screen pixels per the 'Width In Pixels' toggle.");
        private static readonly GUIContent s_WidthIsPixelsLabel = new GUIContent("Width In Pixels",
            "0 = width in world meters, 1 = width in screen pixels (constant on-screen thickness).");
        private static readonly GUIContent s_MetersPerPixelLabel = new GUIContent("Meters Per Pixel",
            "World meters covered by one screen pixel; used to convert pixel width to meters.");
        private static readonly GUIContent s_BlurLabel = new GUIContent("Blur (AA feather)",
            "Edge anti-aliasing feather width, in the fwidth-coverage falloff.");

        // Lit surface property set, collected each event in FindProperties (mirrors URP's LitShader).
        private LitGUI.LitProperties litProperties;

        // Cache of the property array so the Map Paint foldout action (which receives only a Material)
        // can resolve MaterialProperty handles. Refreshed every event by FindProperties (URP's pattern).
        private MaterialProperty[] m_Properties;

        private static readonly string[] s_WorkflowModeNames = Enum.GetNames(typeof(LitGUI.WorkflowMode));

        // ── property collection ──────────────────────────────────────────────
        public override void FindProperties(MaterialProperty[] properties)
        {
            base.FindProperties(properties);
            m_Properties = properties;
            litProperties = new LitGUI.LitProperties(properties);
            // LitDetailGUI is internal — the Details foldout is intentionally omitted (the lone scope cut).
        }

        // ── keyword / render-state sync (the constraint: must stay intact) ───
        public override void ValidateMaterial(Material material)
        {
            // Single shading-model callback; the detail-keyword callback is omitted (LitDetailGUI internal).
            SetMaterialKeywords(material, LitGUI.SetMaterialKeywords);
        }

        // ── Surface Options foldout (workflow popup + base options) ──────────
        public override void DrawSurfaceOptions(Material material)
        {
            EditorGUIUtility.labelWidth = 0f;

            if (litProperties.workflowMode != null)
                DoPopup(LitGUI.Styles.workflowModeText, litProperties.workflowMode, s_WorkflowModeNames);

            base.DrawSurfaceOptions(material);
        }

        // ── Surface Inputs foldout (base/normal/metallic/occlusion/emission) ─
        public override void DrawSurfaceInputs(Material material)
        {
            base.DrawSurfaceInputs(material);
            LitGUI.Inputs(litProperties, materialEditor, material);
            DrawEmissionProperties(material, true);
            DrawTileOffset(materialEditor, baseMapProp);
        }

        // ── Advanced foldout (highlights / reflections + base advanced) ──────
        public override void DrawAdvancedOptions(Material material)
        {
            if (litProperties.reflections != null && litProperties.highlights != null)
            {
                materialEditor.ShaderProperty(litProperties.highlights, LitGUI.Styles.highlightsText);
                materialEditor.ShaderProperty(litProperties.reflections, LitGUI.Styles.reflectionsText);
            }

            base.DrawAdvancedOptions(material);
        }

        // ── additional foldout: Map Paint (sits where Details normally would) ─
        public override void FillAdditionalFoldouts(MaterialHeaderScopeList materialScopesList)
        {
            materialScopesList.RegisterHeaderScope(s_MapPaintHeader, MapExpandable.MapPaint, DrawMapPaint);
        }

        private void DrawMapPaint(Material material)
        {
            if (m_Properties == null)
                return;

            DrawProperty("_MapColor", s_MapColorLabel);
            DrawProperty("_Opacity", s_OpacityLabel);

            // Line-only knobs. _Width exists only on MapRenderer/Line, so HasProperty cleanly
            // discriminates the shared CustomEditor between the fill and the line material.
            if (material.HasProperty("_Width"))
            {
                DrawProperty("_Width",          s_WidthLabel);
                DrawProperty("_WidthIsPixels",  s_WidthIsPixelsLabel);
                DrawProperty("_MetersPerPixel", s_MetersPerPixelLabel);
                DrawProperty("_Blur",           s_BlurLabel);
            }
        }

        private void DrawProperty(string propertyName, GUIContent label)
        {
            var prop = FindProperty(propertyName, m_Properties, false);
            if (prop != null)
                materialEditor.ShaderProperty(prop, label);
        }
    }
}
