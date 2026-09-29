using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Off-screen render helper for snapshot tests: renders a <see cref="Camera"/> to an off-screen
    /// <see cref="RenderTexture"/>, reads the pixels back via <c>Texture2D.ReadPixels</c>, and optionally
    /// writes a PNG to <c>Logs/snapshots/</c>. The caller owns the camera and must dispose the renderer to
    /// release the <see cref="RenderTexture"/>.
    /// </summary>
    /// <example><code>
    ///   using var snap = new SnapshotRenderer(512, 512);
    ///   snap.Render(myCamera);
    ///   snap.WritePng("my-snapshot.png");
    ///   Frame frame = snap.Pixels;   // row-major, bottom-left origin
    /// </code></example>
    public sealed class SnapshotRenderer : IDisposable
    {
        private readonly int            _width;
        private readonly int            _height;
        private RenderTexture           _rt;
        private Texture2D               _tex;
        private bool                    _rendered;

        /// <summary>The colour the target holds before <see cref="Render"/>. No test background or ink is this value.</summary>
        private static readonly Color32 Sentinel = new Color32(3, 251, 7, 255);

        /// <summary>Width of the render target in pixels.</summary>
        public int Width  => _width;

        /// <summary>Height of the render target in pixels.</summary>
        public int Height => _height;

        /// <summary>
        /// Decoded pixels from the last <see cref="Render"/> call, row-major with a <b>BOTTOM-left origin</b>
        /// (Unity's <see cref="Texture2D.ReadPixels"/> convention; this class does not flip, unlike
        /// <c>SpriteSheet</c>). A test that asserts a vertical DIRECTION must read it bottom-up. Default (null
        /// <see cref="MapRenderer.Tests.Frame.Pixels"/>) until <see cref="Render"/> runs.
        /// </summary>
        public Frame Pixels { get; private set; }

        /// <param name="width">Width of the off-screen render target.</param>
        /// <param name="height">Height of the off-screen render target.</param>
        public SnapshotRenderer(int width = 512, int height = 512)
        {
            _width  = width;
            _height = height;

            // Depth = 24 so the Z-buffer exists and mesh geometry renders correctly.
            // RenderTextureFormat.ARGB32 is universally supported, including in batchmode.
            _rt  = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            _rt.Create();

            // TextureFormat.RGBA32 for ReadPixels.
            _tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        /// <summary>
        /// Renders <paramref name="camera"/> to the off-screen <see cref="RenderTexture"/> and
        /// reads back the pixels into <see cref="Pixels"/>.
        ///
        /// The camera's existing <c>targetTexture</c> is saved and restored.
        /// <c>RenderTexture.active</c> is also saved and restored.
        /// </summary>
        /// <exception cref="InvalidOperationException">The camera wrote nothing to the target: every pixel
        /// still holds the pre-render sentinel, so the readback would return stale memory.</exception>
        public void Render(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (_rt == null)    throw new ObjectDisposedException(nameof(SnapshotRenderer));

            RenderTexture prevTarget = camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            try
            {
                camera.targetTexture = _rt;
                RenderTexture.active = _rt;
                GL.Clear(true, true, Sentinel);
                // The target stores the clear colour through the colour space's conversion, so read back what it
                // actually holds rather than trusting the Color32 that went in.
                _tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, recalculateMipMaps: false);
                Color32 cleared = _tex.GetPixel(0, 0);
                camera.Render();

                RenderTexture.active = _rt;
                // ReadPixels reads from RenderTexture.active; rect covers the full target.
                _tex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0, recalculateMipMaps: false);
                _tex.Apply(updateMipmaps: false);

                Color32[] pixels = _tex.GetPixels32();
                if (AllEqual(pixels, cleared))
                    throw new InvalidOperationException(
                        "camera.Render() did not write the target: every pixel still holds the pre-clear sentinel");

                Pixels    = new Frame(pixels, _width, _height);
                _rendered = true;
            }
            finally
            {
                camera.targetTexture  = prevTarget;
                RenderTexture.active  = prevActive;
            }
        }

        private static bool AllEqual(Color32[] pixels, Color32 value)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                if (pixel.r != value.r || pixel.g != value.g || pixel.b != value.b || pixel.a != value.a) return false;
            }
            return true;
        }

        /// <summary>
        /// Encodes the last rendered frame as PNG and writes it under
        /// <c>&lt;repoRoot&gt;/Logs/snapshots/&lt;filename&gt;</c>.
        ///
        /// The snapshots directory is created if it does not exist.
        /// </summary>
        /// <param name="filename">File name only (e.g. "world-fill.png"); no path.</param>
        /// <returns>Absolute path of the written file.</returns>
        public string WritePng(string filename)
        {
            if (!_rendered)
                throw new InvalidOperationException("Call Render() before WritePng().");
            if (_tex == null)
                throw new ObjectDisposedException(nameof(SnapshotRenderer));

            string snapshotsDir = GetSnapshotsDir();
            Directory.CreateDirectory(snapshotsDir);

            string path = Path.Combine(snapshotsDir, filename);

            // Encode from the Texture2D (already populated by Render()).
            byte[] png = ImageConversion.EncodeToPNG(_tex);
            File.WriteAllBytes(path, png);

            return path;
        }

        /// <summary>
        /// Absolute path to <c>&lt;repoRoot&gt;/Logs/snapshots/</c>.
        /// Resolved via <c>Application.dataPath</c> (project root = parent of <c>Assets/</c>).
        /// </summary>
        public static string GetSnapshotsDir()
        {
            // Application.dataPath = "<projectRoot>/Assets"
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            return Path.Combine(projectRoot, "Logs", "snapshots");
        }

        /// <summary>
        /// Encodes an arbitrary RGBA32 buffer (same bottom-left-origin convention as <see cref="Pixels"/>)
        /// to <c>Logs/snapshots/&lt;filename&gt;</c> via a throwaway <see cref="Texture2D"/>. Needed for
        /// artifacts that never went through this renderer's own <c>_tex</c> — e.g. a synthesised diff buffer,
        /// or a second render's pixels — where <see cref="WritePng"/> (which encodes only <c>_tex</c>)
        /// does not apply.
        /// </summary>
        /// <param name="frame">The pixels to encode.</param>
        /// <param name="filename">File name only (e.g. "gv0-fill.actual.png"); no path.</param>
        /// <returns>Absolute path of the written file.</returns>
        public static string WritePngFromRgba32(Frame frame, string filename)
        {
            string snapshotsDir = GetSnapshotsDir();
            Directory.CreateDirectory(snapshotsDir);
            string path = Path.Combine(snapshotsDir, filename);
            File.WriteAllBytes(path, EncodeRgba32ToPng(frame));
            return path;
        }

        /// <summary>
        /// Encodes <paramref name="frame"/> (bottom-left origin, same convention as <see cref="Pixels"/>)
        /// to PNG bytes via a throwaway <see cref="Texture2D"/>. Shared by <see cref="WritePngFromRgba32"/>
        /// and <c>GoldenImage</c>'s bake path (which writes to a different directory, so it cannot reuse
        /// <see cref="WritePngFromRgba32"/> directly).
        /// </summary>
        internal static byte[] EncodeRgba32ToPng(Frame frame)
        {
            var tex = new Texture2D(frame.Width, frame.Height, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(frame.Pixels);
                tex.Apply(updateMipmaps: false);
                return ImageConversion.EncodeToPNG(tex);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// Releases the <see cref="RenderTexture"/> and <see cref="Texture2D"/>. Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            if (_rt != null)
            {
                _rt.Release();
                UnityEngine.Object.DestroyImmediate(_rt);
                _rt = null;
            }
            if (_tex != null)
            {
                UnityEngine.Object.DestroyImmediate(_tex);
                _tex = null;
            }
        }
    }
}
