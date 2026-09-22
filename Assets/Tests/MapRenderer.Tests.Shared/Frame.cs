using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A decoded RGBA32 pixel buffer that knows its own shape.
    ///
    /// <para>Row-major, <b>BOTTOM-left origin</b>: row 0 is the BOTTOM scanline, row index grows UPWARD on
    /// screen. This is Unity's native <c>Texture2D.GetPixels32()</c>/<c>ReadPixels</c> convention (see
    /// <see cref="SnapshotRenderer.Pixels"/>). A vertical-direction assertion must read this as
    /// bottom-up.</para>
    /// </summary>
    public readonly struct Frame
    {
        public Color32[] Pixels { get; }
        public int        Width  { get; }
        public int        Height { get; }

        public Frame(Color32[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width  = width;
            Height = height;
        }

        /// <summary>Pixel at column <paramref name="x"/>, row <paramref name="y"/> (bottom-left origin).</summary>
        public Color32 this[int x, int y] => Pixels[y * Width + x];

        /// <summary>A deep copy — an independent <see cref="Pixels"/> array — so a later re-render through
        /// the same <see cref="SnapshotRenderer"/> cannot retroactively change a frame already captured for
        /// comparison.</summary>
        public Frame Clone() => new Frame((Color32[])Pixels.Clone(), Width, Height);
    }
}
