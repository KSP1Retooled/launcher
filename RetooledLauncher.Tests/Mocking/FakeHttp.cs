using System.Net;
using System.Text;

namespace KSP.Retooled.Launcher.Tests.Mocking;

/// <summary>
/// Answers requests from a map of URL to response.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> requests = [];
    public readonly Dictionary<string, Func<HttpResponseMessage>> routes = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                           CancellationToken cancellationToken)
    {
        this.requests.Add(request);
        return Task.FromResult(this.routes.TryGetValue(request.RequestUri!.ToString(), out Func<HttpResponseMessage>? route)
                                   ? route()
                                   : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public void Json(string url, string json, string? next = null) => this.routes[url] = () =>
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (next != null)
        {
            response.Headers.Add("Link",
                                 $"<{next}>; rel=\"next\", <https://api.github.com/x?page=9>; rel=\"last\"");
        }

        return response;
    };

    public void Bytes(string url, byte[] bytes) => this.routes[url] = () =>
                                                       new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}
