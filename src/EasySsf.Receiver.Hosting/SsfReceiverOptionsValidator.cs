using EasySsf.Core.Event;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.Hosting;

/// <summary>
/// Validates <see cref="SsfReceiverOptions"/> when they are first used or, with
/// <c>ValidateOnStart</c>, when the host starts: every configured transmitter is checked the way
/// <see cref="SsfTransmitterFactory"/> checks it, and the event aliases are registered.
/// </summary>
public sealed class SsfReceiverOptionsValidator : IValidateOptions<SsfReceiverOptions>
{
    public ValidateOptionsResult Validate(string? name, SsfReceiverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }
        List<string> failures = [];
        failures.AddRange(RegisterEventAliases(options));
        IReadOnlyDictionary<string, SsfTransmitterOptions>? transmitters = null;
        try
        {
            transmitters = options.ConfiguredTransmitters();
        }
        catch (SsfReceiverConfigurationException ex)
        {
            failures.Add(ex.Message);
        }
        if (transmitters is not null)
        {
            if (transmitters.Count == 0)
            {
                failures.Add($"{SsfReceiverDefaults.ConfigurationSection}:TransmitterIssuer: The issuer of the SSF transmitter must be configured,"
                    + $" or transmitters under {SsfReceiverDefaults.ConfigurationSection}:Transmitters. Set {SsfReceiverDefaults.ConfigurationSection}:Enabled=false"
                    + " to switch the receiver off.");
            }
            Dictionary<string, string> byIssuer = new(StringComparer.Ordinal);
            foreach ((string transmitterName, SsfTransmitterOptions transmitter) in transmitters)
            {
                failures.AddRange(SsfTransmitterFactory.Validate(transmitterName, transmitter));
                if (transmitter.TransmitterIssuer is { } issuer && !byIssuer.TryAdd(issuer, transmitterName))
                {
                    failures.Add($"{SsfReceiverDefaults.ConfigurationSection}:Transmitters: The transmitters '{transmitterName}' and"
                        + $" '{byIssuer[issuer]}' have the same issuer {issuer}");
                }
            }
        }
        if (string.IsNullOrWhiteSpace(options.Push.EndpointPath) || !options.Push.EndpointPath.StartsWith('/'))
        {
            failures.Add($"{SsfReceiverDefaults.ConfigurationSection}:Push:EndpointPath: The path of the push endpoint must start with '/'"
                + $" (value: '{options.Push.EndpointPath}')");
        }
        if (options.Dedup.Capacity <= 0 || options.Dedup.Lease <= TimeSpan.Zero)
        {
            failures.Add($"{SsfReceiverDefaults.ConfigurationSection}:Dedup: Capacity and Lease must be positive");
        }
        if (options.Http.Timeout <= TimeSpan.Zero)
        {
            failures.Add($"{SsfReceiverDefaults.ConfigurationSection}:Http:Timeout: must be positive");
        }
        if (options.Revocation.Ttl <= TimeSpan.Zero)
        {
            failures.Add($"{SsfReceiverDefaults.ConfigurationSection}:Revocation:Ttl: must be positive");
        }
        return (failures.Count == 0) ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Registers the configured event type aliases, so that they resolve wherever an event type is
    /// named. Registering the same alias again does nothing.
    /// </summary>
    /// <returns>the aliases that could not be registered, with the reason</returns>
    public static IReadOnlyList<string> RegisterEventAliases(SsfReceiverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        foreach ((string alias, string uri) in options.EventAliases)
        {
            try
            {
                SsfEventTypes.RegisterAlias(alias, uri);
            }
            catch (ArgumentException ex)
            {
                failures.Add($"{SsfReceiverDefaults.ConfigurationSection}:EventAliases:{alias}: {ex.Message}");
            }
        }
        return failures;
    }

    /// <summary>
    /// Throws if the options are not usable, with every failure in the message.
    /// </summary>
    /// <exception cref="SsfReceiverConfigurationException">if the options are not usable</exception>
    public static void ThrowIfInvalid(SsfReceiverOptions options)
    {
        ValidateOptionsResult result = new SsfReceiverOptionsValidator().Validate(Options.DefaultName, options);
        if (result.Failed)
        {
            throw new SsfReceiverConfigurationException(SsfReceiverDefaults.ConfigurationSection, null,
                string.Join(Environment.NewLine, result.Failures ?? [result.FailureMessage ?? "invalid"]));
        }
    }
}
