using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// The verification of <see cref="OnlineTestData"/>: bytes that are not the pinned build never reach a test, from the cache or from a source, and a
    /// source that cannot answer ends the test as inconclusive, never red. Needs no network: every host here is on this machine.
    /// </summary>
    [TestFixture]
    public class OnlineTestDataTests
    {
        private static readonly TileId Tile = new TileId { Z = 6, X = 34, Y = 21 };

        /// <summary>
        /// A truncated cache entry is deleted and the test ends inconclusive when no source answers. A source that serves other bytes is refused, writes
        /// nothing to the cache, and also ends the test as inconclusive.
        /// </summary>
        [Test]
        public void BytesThatAreNotThePinnedBuild_AreRefused_FromTheCacheAndFromASource()
        {
            string cache = Path.Combine(Path.GetTempPath(), "umr-online-test-data-" + Guid.NewGuid().ToString("N"));
            string entry = Path.Combine(cache, "tiles", "6", "34", "21.pbf");
            string previousRemote = Environment.GetEnvironmentVariable("UMR_TEST_TILES_REMOTE_URL");
            string previousLocal = Environment.GetEnvironmentVariable("UMR_TEST_TILES_URL");
            string previousCache = OnlineTestData.CacheRoot;
            int closedPort = FreePort();
            int servingPort = FreePort();
            using var listener = new HttpListener();
            try
            {
                OnlineTestData.CacheRoot = cache;
                Environment.SetEnvironmentVariable("UMR_TEST_TILES_REMOTE_URL", $"http://localhost:{closedPort}/{{z}}/{{x}}/{{y}}.pbf");
                Environment.SetEnvironmentVariable("UMR_TEST_TILES_URL", $"http://localhost:{closedPort}");

                // The cache entry is cut short, and the only sources refuse the connection.
                Directory.CreateDirectory(Path.GetDirectoryName(entry));
                File.WriteAllBytes(entry, new byte[] { 0x1A, 0x03, 0x0A });
                var cut = Assert.Throws<InconclusiveException>(() => OnlineTestData.Tile(Tile));
                StringAssert.Contains("not the pinned build", cut.Message, "the truncated entry must be named as bad bytes, not as a failure of the test");
                Assert.IsFalse(File.Exists(entry), "a cache entry with the wrong bytes must be deleted");

                // A source that answers with other bytes.
                listener.Prefixes.Add($"http://localhost:{servingPort}/");
                listener.Start();
                var serving = new Thread(() =>
                {
                    try
                    {
                        while (listener.IsListening)
                        {
                            HttpListenerContext context = listener.GetContext();
                            byte[] body = { 1, 2, 3, 4 };
                            context.Response.OutputStream.Write(body, 0, body.Length);
                            context.Response.Close();
                        }
                    }
                    catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException || ex is InvalidOperationException) { }
                }) { IsBackground = true };
                serving.Start();
                Environment.SetEnvironmentVariable("UMR_TEST_TILES_REMOTE_URL", $"http://localhost:{servingPort}/{{z}}/{{x}}/{{y}}.pbf");
                var wrong = Assert.Throws<InconclusiveException>(() => OnlineTestData.Tile(Tile));
                StringAssert.Contains("not the pinned build", wrong.Message);
                Assert.IsFalse(File.Exists(entry), "bytes that fail the pinned hash must never be written to the cache");
            }
            finally
            {
                listener.Close();
                OnlineTestData.CacheRoot = previousCache;
                Environment.SetEnvironmentVariable("UMR_TEST_TILES_REMOTE_URL", previousRemote);
                Environment.SetEnvironmentVariable("UMR_TEST_TILES_URL", previousLocal);
                if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
