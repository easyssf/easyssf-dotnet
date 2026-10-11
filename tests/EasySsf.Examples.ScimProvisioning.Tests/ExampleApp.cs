using EasySsf.Testing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EasySsf.Examples.ScimProvisioning.Tests;

/// <summary>
/// The example application against a <see cref="TestTransmitter"/>, on a random port, polling fast.
/// </summary>
public sealed class ExampleApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ExampleApp(WebApplication app, TestTransmitter transmitter, Uri baseAddress)
    {
        _app = app;
        Transmitter = transmitter;
        Client = new HttpClient { BaseAddress = baseAddress };
    }

    public TestTransmitter Transmitter { get; }

    public HttpClient Client { get; }

    public UserDirectory Directory => _app.Services.GetRequiredService<UserDirectory>();

    public static async Task<ExampleApp> StartAsync(bool demo, bool autoplay = false)
    {
        TestTransmitter transmitter = new();
        WebApplication app = ScimProvisioningApplication.Build([], transmitter, builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Urls"] = "http://127.0.0.1:0",
                ["EasySsf:Receiver:Poll:Interval"] = "00:00:00.200",
                ["EasySsf:Receiver:Poll:InitialDelay"] = "00:00:00.100",
                ["Demo:Enabled"] = demo.ToString(),
                ["Demo:Autoplay"] = autoplay.ToString(),
                ["Logging:LogLevel:Default"] = "Warning",
            });
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        ExampleApp example = new(app, transmitter, new Uri(address));
        await Eventually.UntilAsync(() => transmitter.Streams.Count > 0, TimeSpan.FromSeconds(10));
        return example;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Transmitter.Dispose();
    }
}
