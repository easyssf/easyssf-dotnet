using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.Hosting;

/// <summary>
/// Starts the background work of the receiver with the host and stops it when the host shuts
/// down: looking up or registering the streams and polling the transmitters.
/// </summary>
public sealed class SsfReceiverHostedService(IServiceProvider services, IOptions<SsfReceiverOptions> options,
        ILogger<SsfReceiverHostedService> logger) : IHostedService
{
    private SsfTransmitters? _transmitters;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("The SSF receiver is disabled ({Section}:Enabled=false)", SsfReceiverDefaults.ConfigurationSection);
            return Task.CompletedTask;
        }
        _transmitters = services.GetService(typeof(SsfTransmitters)) as SsfTransmitters
            ?? throw new InvalidOperationException("SsfTransmitters is not registered, call AddSsfReceiver()");
        _transmitters.Start();
        logger.LogInformation("SSF receiver started with {Count} transmitter(s): {Transmitters}", _transmitters.All.Count,
            string.Join(", ", _transmitters.All));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_transmitters is not null)
        {
            await _transmitters.StopAsync(cancellationToken).ConfigureAwait(false);
            _transmitters = null;
        }
    }
}
