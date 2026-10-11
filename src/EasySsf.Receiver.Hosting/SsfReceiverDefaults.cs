namespace EasySsf.Receiver.Hosting;

/// <summary>
/// The names the hosting integration uses.
/// </summary>
public static class SsfReceiverDefaults
{
    /// <summary>
    /// The configuration section <see cref="SsfReceiverOptions"/> is bound from.
    /// </summary>
    public const string ConfigurationSection = "EasySsf:Receiver";

    /// <summary>
    /// The name of the <see cref="HttpClient"/> the receiver calls the transmitters with
    /// (<c>IHttpClientFactory</c>): metadata, JWK Sets, tokens and stream management.
    /// </summary>
    public const string HttpClientName = "EasySsf.Receiver";

    /// <summary>
    /// The name of the <see cref="HttpClient"/> the pollers call the transmitters with; its timeout
    /// allows for a long poll.
    /// </summary>
    public const string PollHttpClientName = "EasySsf.Receiver.Poll";

    /// <summary>
    /// The name of the health check.
    /// </summary>
    public const string HealthCheckName = "easyssf";
}
