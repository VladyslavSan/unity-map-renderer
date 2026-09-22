// Unity EditMode only — reads the committed fixture off Application.dataPath.

using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Shared reader for the committed sample MVT tile (<c>Assets/Fixtures/sample-tile.bytes</c>).
    /// </summary>
    internal static class SampleTileFixture
    {
        /// <summary>The raw bytes of the committed sample MVT tile; asserts the fixture is present.</summary>
        public static byte[] Bytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            FileAssert.Exists(path);
            return File.ReadAllBytes(path);
        }
    }
}
