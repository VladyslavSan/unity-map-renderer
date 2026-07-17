using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace MapRenderer.Demo
{
    /// <summary>
    /// Screen-space attribution credit for the OpenFreeMap "Liberty" basemap (OpenStreetMap /
    /// OpenMapTiles data). Displaying this credit is a licence condition whenever the map is shown to
    /// the public — builds, preview videos, screenshots. MapLibre GL draws it automatically; because
    /// we render the basemap ourselves, this component draws it instead.
    ///
    /// <para>Attach it to a GameObject in the <b>public</b> demo scene only (kept off the internal
    /// <c>MapDemo</c> scene). Building the Canvas + Text in code — rather than a hand-placed UI Text —
    /// keeps the exact required wording versioned and review-gated in <see cref="RequiredCredit"/>, so
    /// it can't be silently deleted or mistyped in a scene asset. See <c>THIRD-PARTY-NOTICES.txt</c>
    /// for the underlying licences (OpenStreetMap ODbL, OpenMapTiles CC BY 4.0, OpenFreeMap MIT).</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapAttributionOverlay : MonoBehaviour
    {
        /// <summary>The exact credit OpenFreeMap requires non-MapLibre clients to display. Carries the
        /// OpenMapTiles + OpenStreetMap attributions that the ODbL / CC BY 4.0 terms mandate.</summary>
        public const string RequiredCredit = "OpenFreeMap © OpenMapTiles Data from OpenStreetMap";

        [Tooltip("Credit text drawn in the map corner. Defaults to the licence-required OpenFreeMap/OSM " +
                 "string; only extend it (never drop the OpenStreetMap + OpenMapTiles parts).")]
        [SerializeField]
        private string _credit = RequiredCredit;

        [Tooltip("Font size in reference pixels (1920x1080 reference resolution).")] [SerializeField]
        private int _fontSize = 14;

        [Tooltip("Inset from the bottom-right screen corner, in reference pixels (x = right, y = bottom).")]
        [SerializeField]
        private float2 _marginPx = new float2(8f, 6f);

        private void Start() => BuildOverlay();

        private void BuildOverlay()
        {
            // Unity 6000.x builtin uGUI font. A null font renders NOTHING — i.e. a blank credit that
            // looks wired-up in the hierarchy but silently fails the attribution requirement — so guard it.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                Debug.LogError($"{nameof(MapAttributionOverlay)}: builtin font 'LegacyRuntime.ttf' not " +
                               $"found; the attribution credit will not render. Required credit: {RequiredCredit}");
                return;
            }

            var canvasGo = new GameObject("MapAttributionCanvas");
            canvasGo.transform.SetParent(transform, worldPositionStays: false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue; // sit above the map and any other UI

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight  = 1f; // match height → credit stays legible on wide aspect ratios
            // Deliberately no GraphicRaycaster / EventSystem: this is display-only, never interactive.

            var textGo = new GameObject("Attribution");
            textGo.transform.SetParent(canvasGo.transform, worldPositionStays: false);

            var text = textGo.AddComponent<UnityEngine.UI.Text>();
            text.font               = font;
            text.text               = _credit;
            text.fontSize           = _fontSize;
            text.color              = Color.black;
            text.alignment          = TextAnchor.LowerRight;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow   = VerticalWrapMode.Overflow;
            text.raycastTarget      = false; // display-only, don't eat pointer events

            // A soft light outline keeps the black text legible over both the pale basemap and darker geometry.
            var outline = textGo.AddComponent<Outline>();
            outline.effectColor    = new Color(1f, 1f, 1f, 0.7f);
            outline.effectDistance = new Vector2(1f, -1f);

            // Pin to the bottom-right corner with the configured inset.
            RectTransform rt = text.rectTransform;
            rt.anchorMin        = new Vector2(1f,           0f);
            rt.anchorMax        = new Vector2(1f,           0f);
            rt.pivot            = new Vector2(1f,           0f);
            rt.anchoredPosition = new Vector2(-_marginPx.x, _marginPx.y);
            rt.sizeDelta        = new Vector2(640f,         24f);
        }
    }
}