using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Off-screen render helper for snapshot tests.
    ///
    /// Renders a <see cref="Camera"/> to an off-screen <see cref="RenderTexture"/> (NOT the
    /// screen / framebuffer), reads back the pixels via <c>Texture2D.ReadPixels</c>, and optionally
    /// writes a PNG to <c>Logs/snapshots/</c>.
    ///
    /// No <c>[Test]</c> attribute — this is a test utility class, not a test itself.
    ///
    /// Usage pattern (caller is responsible for camera lifecycle):
    /// <code>
    ///   using var snap = new SnapshotRenderer(512, 512);
    ///   snap.Render(myCamera);
    ///   snap.WritePng("my-snapshot.png");
    ///   byte[] raw = snap.RawPixels;   // RGBA32, row-major, top-left origin
    /// </code>
    ///
    /// The caller must dispose the renderer to release the <see cref="RenderTexture"/>.
    /// </summary>
    public sealed class SnapshotRenderer : IDisposable
    {
        private readonly int            _width;
        private readonly int            _height;
        private RenderTexture           _rt;
        private Texture2D               _tex;
        private bool                    _rendered;

        /// <summary>Width of the render target in pixels.</summary>
        public int Width  => _width;

        /// <summary>Height of the render target in pixels.</summary>
        public int Height => _height;

        /// <summary>
        /// Raw RGBA32 pixel data from the last <see cref="Render"/> call. Row-major, <b>BOTTOM-left
        /// origin</b>: row 0 is the BOTTOM scanline of the image, and row index grows UPWARD on screen.
        /// Null until <see cref="Render"/> has been called.
        ///
        /// <para>That is Unity's native convention for <see cref="Texture2D.ReadPixels"/> +
        /// <see cref="Texture2D.GetRawTextureData"/>, and this class does not flip (unlike
        /// <c>SpriteSheet</c>, which deliberately does). The doc here said "top-left origin" until P5, which
        /// is backwards and cost a debugging round on <c>FillTranslateSnapshotTests</c> — any test asserting
        /// a vertical DIRECTION must read this as bottom-up. Counts, coverage fractions and mean luminance
        /// are orientation-independent, which is why nothing caught it sooner.</para>
        /// </summary>
        public byte[] RawPixels { get; private set; }

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
        /// reads back the pixels into <see cref="RawPixels"/>.
        ///
        /// The camera's existing <c>targetTexture</c> is saved and restored.
        /// <c>RenderTexture.active</c> is also saved and restored.
        /// </summary>
        public void Render(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (_rt == null)    throw new ObjectDisposedException(nameof(SnapshotRenderer));

            RenderTexture prevTarget = camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            try
            {
                camera.targetTexture = _rt;
                camera.Render();

                RenderTexture.active = _rt;
                // ReadPixels reads from RenderTexture.active; rect covers the full target.
                _tex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0, recalculateMipMaps: false);
                _tex.Apply(updateMipmaps: false);

                RawPixels = _tex.GetRawTextureData();
                _rendered = true;
            }
            finally
            {
                camera.targetTexture  = prevTarget;
                RenderTexture.active  = prevActive;
            }
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
        /// Returns <c>true</c> if the last rendered pixel buffer is (near-)all-black, which
        /// typically indicates that the GPU context is unavailable in this batch mode session.
        /// When both the world-fill render AND a blank-background render are all-black, the
        /// test should call <c>Assert.Inconclusive</c> (no GPU context) rather than fail.
        /// </summary>
        public bool IsAllBlack(float threshold = 0.97f)
        {
            if (RawPixels == null) return false;
            int totalPx   = _width * _height;
            int blackCount = 0;
            for (int i = 0; i < totalPx; i++)
            {
                int b = i * 4;
                if (RawPixels[b] == 0 && RawPixels[b + 1] == 0 && RawPixels[b + 2] == 0)
                    blackCount++;
            }
            return (float)blackCount / totalPx >= threshold;
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
