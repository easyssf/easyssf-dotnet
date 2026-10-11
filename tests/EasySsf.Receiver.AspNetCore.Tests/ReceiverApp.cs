using EasySsf.Receiver.AspNetCore;
using EasySsf.Receiver.Hosting;
using EasySsf.Testing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EasySsf.Receiver.AspNetCore.Tests;

/// <summary>
/// A minimal API application with the receiver, on a Kestrel port of its own.
/// </summary>
public sealed class ReceiverApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ReceiverApp(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
        Client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = baseAddress };
    }

    public Uri BaseAddress { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<ReceiverApp> StartAsync(IDictionary<string, string?> settings, Action<WebApplicationBuilder>? configure = null,
            Action<WebApplication>? map = null, params TestTransmitter[] transmitters)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddHealthChecks().AddSsfReceiver();
        builder.AddSsfReceiver();
        foreach (string name in new[] { SsfReceiverDefaults.HttpClientName, SsfReceiverDefaults.PollHttpClientName })
        {
            builder.Services.AddHttpClient(name)
                .ConfigurePrimaryHttpMessageHandler(() => new HostRoutingHandler(transmitters))
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        }
        configure?.Invoke(builder);
        WebApplication app = builder.Build();
        if (map is not null)
        {
            map(app);
        }
        else
        {
            app.MapSsfPush();
        }
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ReceiverApp(app, new Uri(address));
    }

    public async Task<HttpResponseMessage> PushAsync(string set, string? authorization = null, string path = "/ssf/push")
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(set, System.Text.Encoding.UTF8, "application/secevent+jwt"),
        };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class HostRoutingHandler(IReadOnlyList<TestTransmitter> transmitters) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri?.Host ?? "";
            TestTransmitter transmitter = transmitters.FirstOrDefault(t => new Uri(t.Issuer).Host == host)
                ?? throw new HttpRequestException("No test transmitter answers for " + host);
            return new HttpMessageInvoker(transmitter.Handler, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }
}
