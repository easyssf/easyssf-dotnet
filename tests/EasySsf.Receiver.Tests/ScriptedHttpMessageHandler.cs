using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace EasySsf.Receiver.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> for tests that answers with scripted responses and records
/// the requests.
/// </summary>
public sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, string Body)> _responses = new();

    public List<(HttpMethod Method, Uri? Uri, HttpRequestHeaders Headers, string? Body)> Requests { get; } = [];

    public ScriptedHttpMessageHandler Respond(HttpStatusCode status, string body)
    {
        return Respond(status, new Dictionary<string, string>(), body);
    }

    public ScriptedHttpMessageHandler Respond(HttpStatusCode status, IReadOnlyDictionary<string, string> headers, string body)
    {
        _responses.Enqueue((status, headers, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = (request.Content is not null) ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
        Requests.Add((request.Method, request.RequestUri, request.Headers, body));
        if (!_responses.TryDequeue(out (HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, string Body) scripted))
        {
            throw new InvalidOperationException($"No scripted response left for {request.Method} {request.RequestUri}");
        }
        HttpResponseMessage response = new(scripted.Status)
        {
            Content = new StringContent(scripted.Body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        foreach ((string name, string value) in scripted.Headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }
}
