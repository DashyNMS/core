using System.Net;
using System.Text;

namespace DesktopNMS.Core.Demo;

/// <summary>
/// Answers HTTP requests from <see cref="DemoResponder"/> in the same process
/// (#119), so an app's real <see cref="Api.LibreNmsTransport"/> and client run
/// a demo with no listener: <see cref="DemoResponder.Connection"/> carries one.
/// It never touches the network, whatever address a request names.
/// </summary>
public sealed class DemoMessageHandler(DemoResponder responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A write's body is forgotten, so it isn't read: a failing stream can't turn the demo's success into an error.
        var path = request.RequestUri is { IsAbsoluteUri: true } uri ? uri.PathAndQuery : request.RequestUri?.OriginalString ?? "/";
        var answer = responder.Respond(request.Method.Method, path);
        return Task.FromResult(new HttpResponseMessage((HttpStatusCode)answer.Status)
        {
            RequestMessage = request,
            Content = new StringContent(answer.Body, Encoding.UTF8, answer.ContentType),
        });
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();
}
