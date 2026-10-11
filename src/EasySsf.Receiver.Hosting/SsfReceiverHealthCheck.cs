using EasySsf.Core.Metadata;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.Hosting;

/// <summary>
/// Reports whether the receiver is in contact with its transmitters, from what the receiver already
/// knows. A transmitter is never called for the health check.
/// <list type="bullet">
/// <item><see cref="HealthStatus.Healthy"/>: every transmitter was reached, that is its metadata was
/// retrieved (PUSH) or the last poll succeeded (POLL), and its stream, if it is looked up or
/// managed, is registered.</item>
/// <item><see cref="HealthStatus.Unhealthy"/>: a last poll failed, or a stream cannot be used.</item>
/// <item><see cref="HealthStatus.Degraded"/>: no contact with a transmitter yet. The application
/// starts without it.</item>
/// </list>
/// With one transmitter its details are the data of the result; with several, the details of each
/// are listed under its name.
/// </summary>
public sealed class SsfReceiverHealthCheck(IServiceProvider services, IOptions<SsfReceiverOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Check());
    }

    /// <summary>
    /// The health of the receiver as of now.
    /// </summary>
    public HealthCheckResult Check()
    {
        if (!options.Value.Enabled)
        {
            return HealthCheckResult.Healthy("The SSF receiver is disabled");
        }
        SsfTransmitters transmitters = services.GetService(typeof(SsfTransmitters)) as SsfTransmitters
            ?? throw new InvalidOperationException("SsfTransmitters is not registered, call AddSsfReceiver()");
        IReadOnlyList<SsfTransmitter> all = transmitters.All;
        Dictionary<string, object> data = new(StringComparer.Ordinal);
        HealthStatus status;
        if (all.Count == 1)
        {
            status = Status(all[0], data);
        }
        else
        {
            status = HealthStatus.Healthy;
            foreach (SsfTransmitter transmitter in all)
            {
                Dictionary<string, object> details = new(StringComparer.Ordinal);
                HealthStatus own = Status(transmitter, details);
                details["status"] = own.ToString();
                data[transmitter.Name] = details;
                status = Worse(status, own);
            }
        }
        return new HealthCheckResult(status, Description(status), data: data);
    }

    private static string Description(HealthStatus status)
    {
        return status switch
        {
            HealthStatus.Healthy => "In contact with the SSF transmitter(s)",
            HealthStatus.Unhealthy => "An SSF transmitter cannot be polled or its stream cannot be used",
            _ => "No contact with an SSF transmitter yet",
        };
    }

    private static HealthStatus Status(SsfTransmitter transmitter, Dictionary<string, object> details)
    {
        HealthStatus status = HealthStatus.Degraded;
        details["transmitter"] = transmitter.Issuer;
        details["delivery"] = (transmitter.Poller is not null) ? "poll" : "push";

        SsfTransmitterMetadata? metadata = transmitter.MetadataResolver.ResolvedMetadata;
        details["metadata"] = (metadata is not null) ? "resolved" : "not retrieved yet";
        if (metadata is not null)
        {
            Put(details, "jwksUri", metadata.JwksUri?.ToString());
            status = HealthStatus.Healthy;
        }

        details["stream"] = transmitter.ReceiverStream.StreamId ?? "none";
        SsfStreamRegistrar? registrar = transmitter.StreamRegistrar;
        if (registrar is not null)
        {
            SsfStreamRegistrarState state = registrar.State;
            details["streamRegistration"] = state.ToString().ToLowerInvariant();
            Put(details, "streamRegistrationError", registrar.LastError);
            if (state == SsfStreamRegistrarState.Failed)
            {
                status = HealthStatus.Unhealthy;
            }
            else if (state != SsfStreamRegistrarState.Registered)
            {
                status = HealthStatus.Degraded;
            }
        }

        SsfPoller? poller = transmitter.Poller;
        if (poller is not null)
        {
            DateTimeOffset? lastSuccess = poller.LastSuccessfulPollAt;
            string? error = poller.LastPollError;
            Put(details, "lastPoll", poller.LastPollAt?.ToString("O"));
            Put(details, "lastSuccessfulPoll", lastSuccess?.ToString("O"));
            Put(details, "pollError", error);
            details["pendingAcks"] = poller.PendingAckCount;
            if (poller.PausedUntil > DateTimeOffset.UtcNow)
            {
                details["pausedUntil"] = poller.PausedUntil.ToString("O");
            }
            if (error is not null)
            {
                status = HealthStatus.Unhealthy;
            }
            else if (lastSuccess is null && status != HealthStatus.Unhealthy)
            {
                status = HealthStatus.Degraded;
            }
        }
        return status;
    }

    private static void Put(Dictionary<string, object> details, string key, string? value)
    {
        if (value is not null)
        {
            details[key] = value;
        }
    }

    private static HealthStatus Worse(HealthStatus current, HealthStatus other)
    {
        if (current == HealthStatus.Unhealthy || other == HealthStatus.Unhealthy)
        {
            return HealthStatus.Unhealthy;
        }
        if (current == HealthStatus.Degraded || other == HealthStatus.Degraded)
        {
            return HealthStatus.Degraded;
        }
        return HealthStatus.Healthy;
    }
}

/// <summary>
/// Registers the health check of the receiver.
/// </summary>
public static class SsfReceiverHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds the <see cref="SsfReceiverHealthCheck"/> under the name <see cref="SsfReceiverDefaults.HealthCheckName"/>.
    /// </summary>
    /// <param name="builder">the health checks of the application</param>
    /// <param name="name">the name of the check, <see cref="SsfReceiverDefaults.HealthCheckName"/> if <see langword="null"/></param>
    /// <param name="failureStatus">the status reported when the check throws, <see cref="HealthStatus.Unhealthy"/> if <see langword="null"/></param>
    /// <param name="tags">tags to filter the check by</param>
    public static IHealthChecksBuilder AddSsfReceiver(this IHealthChecksBuilder builder, string? name = null, HealthStatus? failureStatus = null,
            IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<SsfReceiverHealthCheck>(name ?? SsfReceiverDefaults.HealthCheckName, failureStatus, tags ?? []);
    }
}
