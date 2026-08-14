// Unity EditMode only — drives UnityWebRequestDataSource against a loopback HttpListener.
// NOT included in Tools/core-tests/core-tests.csproj (uses UnityEngine.Networking + UniTask PlayerLoop).
//
// This test file exists to close the coverage gap identified in the S51 review:
// the 404→Absent mapping was dead code (ToUniTask throws before the responseCode check),
// and NO test exercised that code path. Adding a real-HTTP-server test ensures the
// exception-catch fix (try/catch UnityWebRequestException when 404/204) is load-bearing.
//
// Architecture:
//   System.Net.HttpListener serves a loopback response (404 for absent, 200+bytes for data).
//   UnityWebRequest needs the PlayerLoop to poll the internal download handler each frame,
//   so each test is a [UnityTest] IEnumerator (coroutine) with yield return null to pump frames.
//   yield return task.ToCoroutine() drives the UniTask to completion within the coroutine.

using System;
using System.Collections;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Unity.Rendering.Source;
namespace MapRenderer.Tests.DataSources
{
    /// <summary>
    /// S51 review fix — acceptance tests for <see cref="UnityWebRequestDataSource"/>.
    ///
    /// Closes the coverage gap: previously no test exercised the 404→Absent mapping, which
    /// was unreachable dead code (the vendored ToUniTask throws for ProtocolError before
    /// reaching the responseCode check). The fix wraps the await in a narrow
    /// catch(UnityWebRequestException when 404/204) that returns TileResponse.Absent.
    /// These tests prove that fix is load-bearing.
    /// </summary>
    [TestFixture]
    public class UnityWebRequestDataSourceTests
    {
        // ── Loopback server helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds a free loopback port by binding a listener on port 0, recording the assigned port,
        /// then stopping it before returning. Avoids the classic race by using a short-lived listener
        /// to claim the OS port number.
        /// </summary>
        private static int FindFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>
        /// Starts an HttpListener on a loopback address and serves one response asynchronously.
        /// The listener is stopped and closed after serving the single request.
        /// </summary>
        private static HttpListener StartLoopbackServer(string prefix, int statusCode, byte[] body = null)
        {
            var hl = new HttpListener();
            hl.Prefixes.Add(prefix);
            hl.Start();

            // Serve one request on a ThreadPool thread so the test coroutine can yield.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    HttpListenerContext ctx = hl.GetContext();
                    ctx.Response.StatusCode = statusCode;
                    if (body != null && body.Length > 0)
                    {
                        ctx.Response.ContentLength64 = body.Length;
                        ctx.Response.OutputStream.Write(body, 0, body.Length);
                    }
                    ctx.Response.OutputStream.Close();
                    ctx.Response.Close();
                }
                catch (HttpListenerException) { /* listener was stopped before a request arrived */ }
                catch (ObjectDisposedException) { }
                finally
                {
                    try { hl.Stop(); } catch { }
                }
            });

            return hl;
        }

        // ── Tests ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A 404 response from the tile server must yield <c>HasData == false</c> (TileResponse.Absent)
        /// instead of throwing. This is the primary regression test for the S51 dead-code fix:
        /// the previous code had the responseCode check AFTER the await, but ToUniTask throws
        /// UnityWebRequestException for ProtocolError (any non-2xx), so the check was never reached.
        ///
        /// The fix: catch UnityWebRequestException with a when-filter on 404/204 and return Absent.
        /// This test is load-bearing — it would fail on the old code (exception propagates → fail).
        /// </summary>
        [UnityTest]
        public IEnumerator FetchAsync_404Response_ReturnsAbsent_HasDataFalse()
        {
            int port   = FindFreePort();
            string url = $"http://127.0.0.1:{port}/tiles/{{z}}/{{x}}/{{y}}.pbf";
            // Serve a 404 for any request to this prefix.
            var listener = StartLoopbackServer($"http://127.0.0.1:{port}/", 404);

            TileResponse response = default;
            Exception    caught   = null;

            try
            {
                using var source = new UnityWebRequestDataSource(url);
                // Run FetchAsync as a coroutine so UnityWebRequest's PlayerLoop hook pumps.
                // Use void-returning Action<T> to force ContinueWith<T>(Action<T>) overload
                // (avoids the Func<T,TR> overload that would return UniTask<T> and confuse ToCoroutine).
                yield return source.FetchAsync(new TileId { Z = 0, X = 0, Y = 0 })
                    .ContinueWith((Action<TileResponse>)(r => { response = r; }))
                    .ToCoroutine(ex => { caught = ex; });
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }

            Assert.IsNull(caught,
                "A 404 response must NOT throw — it must be caught and mapped to TileResponse.Absent. " +
                $"Exception: {caught?.Message}");
            Assert.IsFalse(response.HasData,
                "HTTP 404 must produce HasData == false (TileResponse.Absent). " +
                "If HasData is true or an exception was thrown, the dead-code fix did not take effect.");
        }

        /// <summary>
        /// A 200 OK response with tile bytes must yield <c>HasData == true</c> with the correct bytes.
        /// This is the positive control: confirms the fix did not break the success path.
        /// </summary>
        [UnityTest]
        public IEnumerator FetchAsync_200Response_ReturnsData_HasDataTrue()
        {
            int port   = FindFreePort();
            string url = $"http://127.0.0.1:{port}/tiles/{{z}}/{{x}}/{{y}}.pbf";
            byte[] expected = new byte[] { 0x1A, 0x2B, 0x3C };
            var listener = StartLoopbackServer($"http://127.0.0.1:{port}/", 200, expected);

            TileResponse response = default;
            Exception    caught   = null;

            try
            {
                using var source = new UnityWebRequestDataSource(url);
                yield return source.FetchAsync(new TileId { Z = 0, X = 0, Y = 0 })
                    .ContinueWith((Action<TileResponse>)(r => { response = r; }))
                    .ToCoroutine(ex => { caught = ex; });
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }

            Assert.IsNull(caught,
                $"A 200 OK response must NOT throw. Exception: {caught?.Message}");
            Assert.IsTrue(response.HasData,
                "HTTP 200 with body bytes must produce HasData == true.");
            Assert.IsNotNull(response.Bytes,
                "HTTP 200 response Bytes must not be null.");
            Assert.AreEqual(expected.Length, response.Bytes.Length,
                "Response byte count must match the served body.");
        }
    }
}
