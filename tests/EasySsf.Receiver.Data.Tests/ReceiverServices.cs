using EasySsf.Receiver.Hosting;
using EasySsf.Testing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EasySsf.Receiver.Data.Tests;

/// <summary>
/// Builds the services of a receiver against a <see cref="TestTransmitter"/>, the way an
/// application does with its configuration (a copy of the helper of the hosting tests).
/// </summary>
public static class ReceiverServices
{
    public static SsfReceiverBuilder AddReceiver(this IServiceCollection services, IDictionary<string, string?> settings, TestTransmitter transmitter)
    {
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        SsfReceiverBuilder builder = services.AddSsfReceiver(configuration.GetSection(SsfReceiverDefaults.ConfigurationSection));
        foreach (string name in new[] { SsfReceiverDefaults.HttpClientName, SsfReceiverDefaults.PollHttpClientName })
        {
            services.AddHttpClient(name)
                .ConfigurePrimaryHttpMessageHandler(() => new RoutingHandler(transmitter))
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        }
        return builder;
    }

    public static Dictionary<string, string?> Settings(TestTransmitter transmitter, params (string Key, string? Value)[] more)
    {
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = transmitter.Issuer,
            ["EasySsf:Receiver:ExpectedAudience"] = TestTransmitter.Audience,
        };
        foreach ((string key, string? value) in more)
        {
            settings[key] = value;
        }
        return settings;
    }

    private sealed class RoutingHandler(TestTransmitter transmitter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return new HttpMessageInvoker(transmitter.Handler, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }
}
