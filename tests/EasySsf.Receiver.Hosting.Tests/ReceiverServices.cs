using EasySsf.Receiver.Hosting;
using EasySsf.Testing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EasySsf.Receiver.Hosting.Tests;

/// <summary>
/// Builds the services of a receiver against one or more <see cref="TestTransmitter"/>s, the way
/// an application does with its configuration.
/// </summary>
public static class ReceiverServices
{
    public static IConfiguration Configuration(IDictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    public static SsfReceiverBuilder AddReceiver(this IServiceCollection services, IDictionary<string, string?> settings,
            params TestTransmitter[] transmitters)
    {
        services.AddLogging();
        SsfReceiverBuilder builder = services.AddSsfReceiver(Configuration(settings).GetSection(SsfReceiverDefaults.ConfigurationSection));
        services.UseTransmitters(transmitters);
        return builder;
    }

    /// <summary>
    /// Routes the HTTP clients of the receiver to the test transmitters, by host.
    /// </summary>
    public static IServiceCollection UseTransmitters(this IServiceCollection services, params TestTransmitter[] transmitters)
    {
        foreach (string name in new[] { SsfReceiverDefaults.HttpClientName, SsfReceiverDefaults.PollHttpClientName })
        {
            services.AddHttpClient(name)
                .ConfigurePrimaryHttpMessageHandler(() => new HostRoutingHandler(transmitters))
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        }
        return services;
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

    /// <summary>
    /// Sends every request to the transmitter whose issuer has the host of the request.
    /// </summary>
    public sealed class HostRoutingHandler(IReadOnlyList<TestTransmitter> transmitters) : HttpMessageHandler
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
