namespace EasySsf.Receiver.Hosting;

/// <summary>
/// Thrown when the configuration of the receiver is not usable: a required setting is missing or
/// a value is not acceptable. The message names the configuration key.
/// </summary>
public class SsfReceiverConfigurationException : Exception
{
    public SsfReceiverConfigurationException(string key, object? value, string reason)
        : base($"{key}: {reason}" + ((value is not null) ? $" (value: '{value}')" : ""))
    {
        Key = key;
        Value = value;
        Reason = reason;
    }

    /// <summary>
    /// The configuration key, for example <c>EasySsf:Receiver:TransmitterIssuer</c>.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// The value that was configured, <see langword="null"/> if none.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Why the value is not acceptable.
    /// </summary>
    public string Reason { get; }
}
