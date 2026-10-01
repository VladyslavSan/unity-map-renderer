// Unity EditMode only — reads a cache folder off Application.dataPath and may call the network.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Real map data for the <c>[Category("Online")]</c> tests. The repo bundles none of it. A request goes through a chain: the cache
    /// folder, then the pinned OpenFreeMap build, then a local server of the test-tile repository, then
    /// <see cref="Assert.Inconclusive(string)"/>. Every byte is checked against the pinned SHA-256 of its file, from the cache or from a source: bytes
    /// that are not the pinned build never reach a test. A host that fails to connect is skipped for the rest of the AppDomain.
    /// See <c>docs/test-conventions.md</c> § "Online tests".
    /// </summary>
    internal static class OnlineTestData
    {
        private const string PinnedBuild       = "20260927_080001_pt";
        private const string LocalUrlVariable  = "UMR_TEST_TILES_URL";
        private const string LocalDefaultUrl   = "http://localhost:8000";
        private const string RemoteUrlVariable = "UMR_TEST_TILES_REMOTE_URL"; // a {z}/{x}/{y} template that replaces the OpenFreeMap one
        private const string RemoteDefaultUrl  = "https://tiles.openfreemap.org/planet/" + PinnedBuild + "/{z}/{x}/{y}.pbf";

        private static readonly TimeSpan LocalTimeout  = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(15);

        // One client for the process. The repo's HttpTransport stalls under a blocking EditMode test, so this runs on a worker thread.
        private static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        // A hash of a file is not map data. These are the SHA-256 values of manifest.json in the test-tile repository.
        private static readonly Dictionary<string, string> PinnedSha256 = new Dictionary<string, string>
        {
            ["tiles/6/34/21.pbf"] = "5525a6ddaf632b7e64eae89faa2592d2bcf58944dbfcf18381bda69b942ad5dc",
            ["tiles/6/38/19.pbf"] = "a0a19f9cd4f3299709522d9faabf2a72263bc2a24dfe0b0cce1d347ebffbb02c",
            ["tiles/9/274/168.pbf"] = "34023cfebe8b72a224cbeaaa975a3595925e03c4d19253811eff21fd79506666",
            ["tiles/6/32/20.pbf"] = "d2f8409c0d96762d1c5bcc532a59aee5a6828aa510b0c41380597292b0aa2b13",
            ["tiles/8/135/80.pbf"] = "f4516e59f367ae45dfaad76f48d7a0988c7689d27cb50c2f0ae99c7151108029",
            ["tiles/8/145/99.pbf"] = "4ba9c4cea24081c1be3fcff4e0775da713a5c512042517d72353400877d2e2da",
            ["tiles/9/279/187.pbf"] = "5afaada1f123bb7d25ffdf98e51ec6e0c0238fce2394ec9329adedc006fb55f3",
            ["tiles/8/220/128.pbf"] = "5c33e85ab9365d26c35b658a1550dec874ca4fc8e9cc56afac1a265a19a6917b",
            ["tiles/8/132/72.pbf"] = "678cfb36b352b493185e9149a1230d5c18c5cd36863a8b6e8d8c4d72f7bdde80",
            ["tiles/8/212/120.pbf"] = "7e32eb9f854978fff397cfc4bc66ddd401aae03bf967748b1dc053158560e63c",
            ["tiles/9/282/150.pbf"] = "ea480b110ea1e802266ddb979a3bd5ed69edbf4ecb7ab7829ca0d7447b507859",
            ["goldens/line-graphwrite-golden-z6-Spherical.json"] = "8a3598a4a327541654ffe7c3842769763eb8689e4e0fa44fc932d3032fb07f05",
            ["goldens/line-graphwrite-golden-z6-WebMercator.json"] = "d71edbf01f7b605e95ea0cee552c4908c6b0885af01e9a094a8a936a85882b4a",
            ["goldens/line-graphwrite-golden-z9-Spherical.json"] = "deca64ddc1e5ad11ba437d93707147a9b9cec6e77c22f0a2a728d1799540385b",
            ["goldens/line-graphwrite-golden-z9-WebMercator.json"] = "04eb9da696314228b5db420d9e74ad45853b4237e5a6f0d0aaed94719d6089b5",
        };

        // Hosts that failed to connect, kept for the lifetime of the AppDomain (a new Editor domain, or a batch run, starts clean), so an
        // unreachable host costs one timeout, not one per tile.
        private static readonly HashSet<string> UnreachableHosts = new HashSet<string>();

        private static string _cacheRootOverride;

        /// <summary>
        /// The tiles of the test corpus, under the file names the committed fixtures once had. The test-tile repository manifest lists the same tiles.
        /// </summary>
        internal static readonly string[] RealTileCorpus =
        {
            "boundary-6-34-21.pbf.bytes", "boundary-6-38-19.pbf.bytes", "boundary-9-274-168.pbf.bytes",
            "water-6-32-20.pbf.bytes", "water-8-135-80.pbf.bytes",
            "water-real-aegean-islands-8-145-99.pbf.bytes", "water-real-croatia-dalmatia-9-279-187.pbf.bytes",
            "water-real-indonesia-rajaampat-8-220-128.pbf.bytes", "water-real-norway-fjords-8-132-72.pbf.bytes",
            "water-real-philippines-palawan-8-212-120.pbf.bytes", "water-real-stockholm-archipelago-9-282-150.pbf.bytes",
        };

        /// <summary>
        /// The corpus the frozen-golden tests walk: <c>sample-tile.bytes</c> first, then every real tile in ordinal file-name order. The
        /// goldens pin the digest of this exact sequence of tiles.
        /// </summary>
        internal static List<(string FileName, byte[] Bytes)> CorpusWithSample()
        {
            var corpus = new List<(string, byte[])> { ("sample-tile.bytes", SampleTileFixture.Bytes()) };
            var names = new List<string>(RealTileCorpus);
            names.Sort(StringComparer.Ordinal);
            foreach (string name in names) corpus.Add((name, Tile(name)));
            return corpus;
        }

        /// <summary>The cache folder, ignored by git and by the Unity importer (the <c>~</c> suffix). A test of this helper points it at a temporary folder.</summary>
        internal static string CacheRoot
        {
            get => _cacheRootOverride ?? Path.Combine(Application.dataPath, "Fixtures", "online~");
            set => _cacheRootOverride = value;
        }

        /// <summary>
        /// The tile a committed fixture name stands for, e.g. <c>boundary-6-34-21.pbf.bytes</c> is tile 6/34/21.
        /// </summary>
        /// <param name="fixtureName">The old fixture file name; the last three numbers are z, x, y.</param>
        internal static byte[] Tile(string fixtureName)
        {
            string[] parts = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fixtureName)).Split('-');
            int n = parts.Length;
            return Tile(new TileId { Z = int.Parse(parts[n - 3]), X = int.Parse(parts[n - 2]), Y = int.Parse(parts[n - 1]) });
        }

        /// <summary>The bytes of tile <paramref name="id"/>. Ends the test as inconclusive when no source gives the pinned bytes.</summary>
        internal static byte[] Tile(TileId id)
        {
            string template = Environment.GetEnvironmentVariable(RemoteUrlVariable) is { Length: > 0 } custom ? custom : RemoteDefaultUrl;
            string remote = template.Replace("{z}", id.Z.ToString()).Replace("{x}", id.X.ToString()).Replace("{y}", id.Y.ToString());
            return Resolve($"tiles/{id.Z}/{id.X}/{id.Y}.pbf", remote);
        }

        /// <summary>The text of a golden file of the test-tile repository. Goldens are not on OpenFreeMap: the cache or the local server serves them.</summary>
        /// <param name="name">The file name under <c>goldens/</c>.</param>
        internal static string Golden(string name) => Encoding.UTF8.GetString(Resolve("goldens/" + name, null));

        /// <summary>
        /// The cache entry, else <paramref name="remoteUrl"/> (when given), else the local server. Each candidate must match the pinned hash; a cache entry
        /// that does not is deleted. Writes a verified download to the cache. Ends the test as inconclusive when nothing verifies.
        /// </summary>
        private static byte[] Resolve(string relative, string remoteUrl)
        {
            if (!PinnedSha256.TryGetValue(relative, out string expected))
            {
                Assert.Inconclusive($"{relative} is not in the pinned test corpus (OnlineTestData.PinnedSha256).");
                return null;
            }

            var tried = new StringBuilder();
            string cachePath = Path.Combine(CacheRoot, relative);
            if (File.Exists(cachePath))
            {
                byte[] cached = File.ReadAllBytes(cachePath);
                if (Matches(cached, expected)) return cached;
                File.Delete(cachePath);
                tried.Append("cache ").Append(cachePath).Append(" (bytes are not the pinned build; entry deleted); ");
            }
            else
            {
                tried.Append("cache ").Append(cachePath).Append(" (absent); ");
            }

            byte[] bytes = null;
            if (remoteUrl != null) bytes = Verified(Fetch(remoteUrl, RemoteTimeout, tried), expected, remoteUrl, tried);
            if (bytes == null)
            {
                string baseUrl = Environment.GetEnvironmentVariable(LocalUrlVariable) is { Length: > 0 } custom ? custom : LocalDefaultUrl;
                string localUrl = baseUrl.TrimEnd('/') + "/" + relative;
                bytes = Verified(Fetch(localUrl, LocalTimeout, tried), expected, localUrl, tried);
            }

            if (bytes == null)
            {
                Assert.Inconclusive($"no source gave the pinned bytes of {relative}. Tried: {tried}" +
                                    "Run Tools/fetch-test-tiles.sh, serve the test-tile repository on localhost, or allow internet.");
                return null;
            }

            WriteCache(cachePath, bytes);
            return bytes;
        }

        private static bool Matches(byte[] bytes, string expectedSha256)
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            var hex = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) hex.Append(b.ToString("x2"));
            return hex.ToString() == expectedSha256;
        }

        private static byte[] Verified(byte[] bytes, string expectedSha256, string url, StringBuilder tried)
        {
            if (bytes == null) return null;
            if (Matches(bytes, expectedSha256)) return bytes;
            tried.Append(url).Append(" (bytes are not the pinned build); ");
            return null;
        }

        /// <summary>Writes through a <c>.part</c> file and moves it into place, so a crash cannot leave a half-written cache entry.</summary>
        private static void WriteCache(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string part = path + ".part";
            File.WriteAllBytes(part, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
        }

        /// <summary>
        /// One GET on a worker thread, bounded by <paramref name="timeout"/>. Null on any source failure, with the reason appended to <paramref name="tried"/>.
        /// A failure to connect marks the host unreachable; an HTTP status such as 404 does not. Only NUnit's own result exceptions pass through.
        /// </summary>
        private static byte[] Fetch(string url, TimeSpan timeout, StringBuilder tried)
        {
            string host = new Uri(url).Authority;
            if (UnreachableHosts.Contains(host))
            {
                tried.Append(url).Append(" (host unreachable earlier in this domain); ");
                return null;
            }

            try
            {
                return Task.Run(async () =>
                {
                    using var cancel = new CancellationTokenSource(timeout);
                    using HttpResponseMessage response = await Client.GetAsync(url, cancel.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        tried.Append(url).Append(" (HTTP ").Append((int)response.StatusCode).Append("); ");
                        return null;
                    }
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not AssertionException && ex is not InconclusiveException)
            {
                UnreachableHosts.Add(host);
                tried.Append(url).Append(" (").Append(ex.GetType().Name).Append("); ");
                return null;
            }
        }
    }
}
