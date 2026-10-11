using EasySsf.Receiver.Hosting;
using EasySsf.Testing;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EasySsf.Examples.ScimProvisioning;

/// <summary>
/// Mirrors the users of a SCIM service provider into a local <see cref="UserDirectory"/> from the
/// SCIM Events (RFC 9967) the provider transmits over SSF. The receiver polls the transmitter and
/// hands the events to <see cref="ScimProvisioningHandler"/>; the directory is published at
/// <c>/users</c>. With a <see cref="TestTransmitter"/> the application plays the service provider
/// as well (<see cref="DemoScimProvider"/>, <c>/demo/scim</c>), unless <c>Demo:Enabled</c> is
/// <see langword="false"/>.
/// </summary>
public static class ScimProvisioningApplication
{
    public static WebApplication Build(string[] args, TestTransmitter? transmitter, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        if (transmitter is not null)
        {
            // the receiver's HTTP clients reach the in-process transmitter without a socket
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["EasySsf:Receiver:TransmitterIssuer"] = transmitter.Issuer });
            foreach (string name in new[] { SsfReceiverDefaults.HttpClientName, SsfReceiverDefaults.PollHttpClientName })
            {
                builder.Services.AddHttpClient(name)
                    .ConfigurePrimaryHttpMessageHandler(() => new TransmitterHandler(transmitter))
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            }
            builder.Services.AddSingleton(transmitter);
        }
        configure?.Invoke(builder);
        builder.Services.AddSingleton<UserDirectory>();
        builder.AddSsfReceiver().AddEventHandler<ScimProvisioningHandler>();
        bool demo = transmitter is not null && builder.Configuration.GetValue("Demo:Enabled", true);
        if (demo)
        {
            builder.Services.TryAddSingleton<DemoScimProvider>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<DemoScimProvider>());
        }
        WebApplication app = builder.Build();
        app.MapUsers();
        if (demo)
        {
            app.MapDemoScimProvider();
        }
        return app;
    }

    /// <summary>
    /// Sends the requests of the receiver to the handler of the transmitter, which the HTTP client
    /// factory must not dispose.
    /// </summary>
    private sealed class TransmitterHandler(TestTransmitter transmitter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return new HttpMessageInvoker(transmitter.Handler, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }
}
