using System.Net;
using System.Text;
using Shoko.VFS.FUSE.Host.Api;

namespace Shoko.VFS.FUSE.Tests.Host;

/// <summary>
/// Verifies <see cref="ShokoRestClient"/>'s retry path creates a fresh
/// <see cref="HttpRequestMessage"/> per attempt — sending the same request
/// twice throws <c>InvalidOperationException</c> from <see cref="HttpClient"/>.
/// </summary>
public sealed class ShokoRestClientRetryTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _calls;

        /// <summary>Number of times <see cref="SendAsync"/> was invoked.</summary>
        public int Calls => _calls;

        /// <summary>HTTP status to return. Tests set this after the first call to simulate a transient error followed by success.</summary>
        public HttpStatusCode FirstStatus { get; set; } = HttpStatusCode.ServiceUnavailable;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref _calls);
            HttpStatusCode status = n == 1 ? FirstStatus : HttpStatusCode.OK;
            string body = status == HttpStatusCode.OK ? "[]" : "{\"err\":\"transient\"}";
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task GetManagedFoldersRecoversFromTransient503()
    {
        var handler = new CountingHandler { FirstStatus = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var client = new ShokoRestClient(http, "http://test/", retries: 2, baseBackoff: TimeSpan.FromMilliseconds(1));

        var folders = await client.GetManagedFoldersAsync();

        Assert.NotNull(folders);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetJsonRecoversFromConnectionReset()
    {
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var client = new ShokoRestClient(http, "http://test/", retries: 1, baseBackoff: TimeSpan.FromMilliseconds(1));

        var result = await client.GetManagedFoldersAsync();

        Assert.NotNull(result);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref _calls);
            if (n == 1)
                throw new HttpRequestException("simulated connection reset");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }
}
