// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Test.Common;
using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

namespace System.Net.Http.Functional.Tests
{
    public class HttpRequestMessageTest : HttpClientHandlerTestBase
    {
        private readonly Version _expectedRequestMessageVersion = HttpVersion.Version11;

        public HttpRequestMessageTest(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void Ctor_Default_CorrectDefaults()
        {
            var rm = new HttpRequestMessage();

            Assert.Equal(HttpMethod.Get, rm.Method);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Null(rm.Content);
            Assert.Null(rm.RequestUri);
        }

        [Fact]
        public void Ctor_RelativeStringUri_CorrectValues()
        {
            var rm = new HttpRequestMessage(HttpMethod.Post, "/relative");

            Assert.Equal(HttpMethod.Post, rm.Method);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Null(rm.Content);
            Assert.Equal(new Uri("/relative", UriKind.Relative), rm.RequestUri);
        }

        [Fact]
        public void Ctor_AbsoluteStringUri_CorrectValues()
        {
            var rm = new HttpRequestMessage(HttpMethod.Post, "http://host/absolute/");

            Assert.Equal(HttpMethod.Post, rm.Method);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Null(rm.Content);
            Assert.Equal(new Uri("http://host/absolute/"), rm.RequestUri);
        }

        [Fact]
        public void Ctor_NullStringUri_Accepted()
        {
            var rm = new HttpRequestMessage(HttpMethod.Put, (string)null);

            Assert.Null(rm.RequestUri);
            Assert.Equal(HttpMethod.Put, rm.Method);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Null(rm.Content);
        }

        [Fact]
        public void Ctor_EmptyStringUri_Accepted()
        {
            var rm = new HttpRequestMessage(HttpMethod.Put, string.Empty);

            Assert.Null(rm.RequestUri);
            Assert.Equal(HttpMethod.Put, rm.Method);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Null(rm.Content);
        }

        [Fact]
        public void Ctor_RelativeUri_CorrectValues()
        {
            var uri = new Uri("/relative", UriKind.Relative);
            var rm = new HttpRequestMessage(HttpMethod.Post, uri);

            Assert.Equal(HttpMethod.Post, rm.Method);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Null(rm.Content);
            Assert.Equal(uri, rm.RequestUri);
        }

        [Fact]
        public void Ctor_AbsoluteUri_CorrectValues()
        {
            var uri = new Uri("http://host/absolute/");
            var rm = new HttpRequestMessage(HttpMethod.Post, uri);

            Assert.Equal(HttpMethod.Post, rm.Method);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Null(rm.Content);
            Assert.Equal(uri, rm.RequestUri);
        }

        [Fact]
        public void Ctor_NullUri_Accepted()
        {
            var rm = new HttpRequestMessage(HttpMethod.Put, (Uri)null);

            Assert.Null(rm.RequestUri);
            Assert.Equal(HttpMethod.Put, rm.Method);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Null(rm.Content);
        }

        [Fact]
        public void Ctor_NullMethod_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new HttpRequestMessage(null, "http://example.com"));
        }

        [Fact]
        public void Ctor_NonHttpUri_DoesNotThrow()
        {
            new HttpRequestMessage(HttpMethod.Put, "ftp://example.com");
        }

        [Fact]
        public void Dispose_DisposeObject_ContentGetsDisposedAndSettersWillThrowButGettersStillWork()
        {
            var rm = new HttpRequestMessage(HttpMethod.Get, "http://example.com");
            var content = new MockContent();
            rm.Content = content;
            Assert.False(content.IsDisposed);

            rm.Dispose();
            rm.Dispose(); // Multiple calls don't throw.

            Assert.True(content.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => { rm.Method = HttpMethod.Put; });
            Assert.Throws<ObjectDisposedException>(() => { rm.RequestUri = null; });
            Assert.Throws<ObjectDisposedException>(() => { rm.Version = new Version(1, 0); });
            Assert.Throws<ObjectDisposedException>(() => { rm.Content = null; });

            // Property getters should still work after disposing.
            Assert.Equal(HttpMethod.Get, rm.Method);
            Assert.Equal(new Uri("http://example.com"), rm.RequestUri);
            Assert.Equal(_expectedRequestMessageVersion, rm.Version);
            Assert.Equal(content, rm.Content);
        }

        [Fact]
        public void Properties_SetPropertiesAndGetTheirValue_MatchingValues()
        {
            var rm = new HttpRequestMessage();

            var content = new MockContent();
            var uri = new Uri("https://example.com");
            var version = new Version(1, 0);
            var method = new HttpMethod("custom");

            rm.Content = content;
            rm.Method = method;
            rm.RequestUri = uri;
            rm.Version = version;

            Assert.Equal(content, rm.Content);
            Assert.Equal(uri, rm.RequestUri);
            Assert.Equal(method, rm.Method);
            Assert.Equal(version, rm.Version);

            Assert.NotNull(rm.Headers);
            Assert.NotNull(rm.Options);
        }

        [Fact]
        public void RequestUri_SetNonHttpUri_DoesNotThrow()
        {
            var rm = new HttpRequestMessage();
            rm.RequestUri = new Uri("ftp://example.com");
        }

        [Fact]
        public void Version_SetToNull_ThrowsArgumentNullException()
        {
            var rm = new HttpRequestMessage();
            Assert.Throws<ArgumentNullException>(() => { rm.Version = null; });
        }

        [Fact]
        public void Method_SetToNull_ThrowsArgumentNullException()
        {
            var rm = new HttpRequestMessage();
            Assert.Throws<ArgumentNullException>(() => { rm.Method = null; });
        }

        [Fact]
        public void ToString_DefaultAndNonDefaultInstance_DumpAllFields()
        {
            var rm = new HttpRequestMessage();
            string expected =
                    "Method: GET, RequestUri: '<null>', Version: " +
                    _expectedRequestMessageVersion.ToString(2) +
                    $", Content: <null>, Headers:{Environment.NewLine}{{{Environment.NewLine}}}";
            Assert.Equal(expected, rm.ToString());

            rm.Method = HttpMethod.Put;
            rm.RequestUri = new Uri("http://a.com/");
            rm.Version = new Version(1, 0);
            rm.Content = new StringContent("content");

            // Note that there is no Content-Length header: The reason is that the value for Content-Length header
            // doesn't get set by StringContent..ctor, but only if someone actually accesses the ContentLength property.
            Assert.Equal(
                "Method: PUT, RequestUri: 'http://a.com/', Version: 1.0, Content: " + typeof(StringContent).ToString() + ", Headers:" + Environment.NewLine +
                $"{{{Environment.NewLine}" +
                "  Content-Type: text/plain; charset=utf-8" + Environment.NewLine +
                "}", rm.ToString());

            rm.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.2));
            rm.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml", 0.1));
            rm.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.5"); // validate this remains unparsed
            rm.Headers.Add("Custom-Request-Header", "value1");
            rm.Content.Headers.Add("Custom-Content-Header", "value2");

            for (int i = 0; i < 2; i++) // make sure ToString() doesn't impact subsequent use
            {
                Assert.Equal(
                    "Method: PUT, RequestUri: 'http://a.com/', Version: 1.0, Content: " + typeof(StringContent).ToString() + ", Headers:" + Environment.NewLine +
                    "{" + Environment.NewLine +
                    "  Accept: text/plain; q=0.2, text/xml; q=0.1" + Environment.NewLine +
                    "  Accept-Language: en-US,en;q=0.5" + Environment.NewLine +
                    "  Custom-Request-Header: value1" + Environment.NewLine +
                    "  Content-Type: text/plain; charset=utf-8" + Environment.NewLine +
                    "  Custom-Content-Header: value2" + Environment.NewLine +
                    "}", rm.ToString());
            }
        }

        [Fact]
        public void ToString_HeadersDumpIsEquivalentToHttpHeadersDump()
        {
            var m = new HttpRequestMessage(HttpMethod.Get, "http://a.org/x");
            m.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.2));
            m.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml", 0.1));
            m.Headers.ConnectionClose = true;
            m.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.5");
            m.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
            m.Headers.TryAddWithoutValidation("Accept-Encoding", "deflate");

            _output.WriteLine(m.Headers.ToString());
            _output.WriteLine(m.ToString());

            // Add indentation:
            string expected = string.Join(Environment.NewLine, m.Headers.ToString().Split(Environment.NewLine).Where(s => s.Length > 0).Select(s => "  " + s));
            _output.WriteLine(expected);

            Assert.Contains(expected, m.ToString());
        }

        [Theory]
        [InlineData("DELETE")]
        [InlineData("OPTIONS")]
        [InlineData("HEAD")]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/86317", typeof(PlatformDetection), nameof(PlatformDetection.IsNodeJS))]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/101115", typeof(PlatformDetection), nameof(PlatformDetection.IsFirefox))]
        public async Task HttpRequest_BodylessMethod_NoContentLength(string method)
        {
            using (HttpClient client = CreateHttpClient())
            {
                await LoopbackServer.CreateServerAsync(async (server, uri) =>
                {
                    var request = new HttpRequestMessage();
                    request.RequestUri = uri;
                    request.Method = new HttpMethod(method);

                    Task<HttpResponseMessage> requestTask = client.SendAsync(request);
                    await server.AcceptConnectionAsync(async connection =>
                    {
                        var requestData = await connection.ReadRequestDataAsync().ConfigureAwait(false);
#if TARGET_BROWSER
                        requestData = await connection.HandleCORSPreFlight(requestData);
#endif

                        Assert.DoesNotContain(requestData.Headers, line => line.Name.StartsWith("Content-length"));

                        await connection.SendResponseAsync();
                        await requestTask;
                    });
                });
            }
        }

        [Fact]
        public async Task HttpRequest_StringContent_WithoutMediaType()
        {
            using (HttpClient client = CreateHttpClient())
            {
                await LoopbackServer.CreateServerAsync(async (server, uri) =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, uri);
                    request.Content = new StringContent("", null, (MediaTypeHeaderValue)null);

                    Task<HttpResponseMessage> requestTask = client.SendAsync(request);
                    await server.AcceptConnectionAsync(async connection =>
                    {
                        var requestData = await connection.ReadRequestDataAsync().ConfigureAwait(false);
#if TARGET_BROWSER
                        requestData = await connection.HandleCORSPreFlight(requestData);
#endif

                        Assert.DoesNotContain(requestData.Headers, line => line.Name.StartsWith("Content-Type"));

                        await connection.SendResponseAsync();
                        await requestTask;
                    });
                });
            }
        }

        [Fact]
        public async Task HttpRequest_BodylessMethod_LargeContentLength()
        {
            using (HttpClient client = CreateHttpClient())
            {
                await LoopbackServer.CreateServerAsync(async (server, uri) =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Head, uri);

                    Task<HttpResponseMessage> requestTask = client.SendAsync(request);

                    await server.AcceptConnectionAsync(async connection =>
                    {
                        // Content-Length greater than 2GB.
                        string response = LoopbackServer.GetConnectionCloseResponse(
                            HttpStatusCode.OK, "Content-Length: 2167849215\r\n\r\n");
                        await connection.SendResponseAsync(response);

                        await requestTask;
                    });

                    using (HttpResponseMessage result = requestTask.Result)
                    {
                        Assert.NotNull(result);
                        Assert.NotNull(result.Content);
                        Assert.Equal(2167849215, result.Content.Headers.ContentLength);
                    }
                });
            }
        }

        #region Helper methods

        private class MockContent : HttpContent
        {
            public bool IsDisposed { get; private set; }

            protected override bool TryComputeLength(out long length)
            {
                throw new NotImplementedException();
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                throw new NotImplementedException();
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                base.Dispose(disposing);
            }
        }

        #endregion
    }
}
