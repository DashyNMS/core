using System.Net;
using System.Text;

namespace DesktopNMS.Core.Demo;

/// <summary>
/// Answers HTTP requests from <see cref="DemoResponder"/> in the same process
/// (#119), so an app's real <see cref="Api.LibreNmsTransport"/> and client run
/// a demo with no listener: give it to <see cref="Api.LibreNmsTransport.UseHandler"/>.
/// It never touches the network, whatever address a request names.
/// </summary>
public sealed class DemoMessageHandler(DemoResponder responder) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var path = request.RequestUri is { IsAbsoluteUri: true } uri ? uri.PathAndQuery : request.RequestUri?.OriginalString ?? "/";
        var answer = responder.Respond(request.Method.Method, path, body);
        return new HttpResponseMessage((HttpStatusCode)answer.Status)
        {
            RequestMessage = request,
            Content = new StringContent(answer.Body, Encoding.UTF8, answer.ContentType),
        };
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();
}
