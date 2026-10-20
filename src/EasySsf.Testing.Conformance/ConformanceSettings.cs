namespace EasySsf.Testing.Conformance;

/// <summary>
/// How the conformance tests start the suite and expose the system under test, taken from
/// environment variables (<c>CTS_SUITE_IMAGE</c>, ...).
/// </summary>
public static class ConformanceSettings
{
    /// <summary>
    /// The suite image.
    /// </summary>
    public const string SuiteImageVariable = "CTS_SUITE_IMAGE";

    /// <summary>
    /// The nginx image of the suite.
    /// </summary>
    public const string SuiteNginxImageVariable = "CTS_SUITE_NGINX_IMAGE";

    /// <summary>
    /// The MongoDB image of the suite.
    /// </summary>
    public const string SuiteMongoDbImageVariable = "CTS_SUITE_MONGODB_IMAGE";

    /// <summary>
    /// The host port the suite listens on. Fixed, as the suite has to know its own URL before it
    /// starts.
    /// </summary>
    public const string SuitePortVariable = "CTS_SUITE_PORT";

    /// <summary>
    /// The port the receiver under test listens on. Fixed, as the suite reaches it by its push URL.
    /// </summary>
    public const string ReceiverPortVariable = "CTS_RECEIVER_PORT";

    /// <summary>
    /// The push URL the suite is to deliver to, when the default of
    /// <c>https://&lt;host as seen from the suite&gt;:&lt;receiver port&gt;/ssf/push</c> does not fit.
    /// </summary>
    public const string ReceiverPushUrlVariable = "CTS_RECEIVER_PUSH_URL";

    /// <summary>
    /// The suite release the default images are, for messages: <c>release-v5.3.2</c> (2026-10-09),
    /// the first release with the SSF receiver test fixes made since <c>release-v5.3.1</c>. The
    /// images are pinned by digest here, the single place to change them.
    /// </summary>
    public const string DefaultSuiteVersion = "release-v5.3.2";

    public const string DefaultSuiteImage = "registry.gitlab.com/openid/conformance-suite@sha256:c795df1b151e0c2f2e40f17a2470991cc38bc91628449e9bb090e2372fe8b822";

    public const string DefaultSuiteNginxImage = "registry.gitlab.com/openid/conformance-suite/nginx@sha256:607458218abb58204201e4d283af90177d6b542535a99a21041d399a1505a6a0";

    /// <summary>
    /// The suite's own compose file runs MongoDB 6.0.13, as the suite does in production. For the
    /// tests here a current release, with far fewer known vulnerabilities, works just as well:
    /// <c>mongo:9.0.4</c>, pinned by digest like the suite images. Not older: MongoDB 8.0 and
    /// newer refuse to start on a Linux kernel from 6.19 up to 7.0.13 (a tcmalloc
    /// incompatibility, SERVER-121912), and before 9.0.4 they took Ubuntu's 7.0 kernels, the ones
    /// of GitHub's runners since October 2026, for 7.0.0 (SERVER-131779).
    /// </summary>
    public const string DefaultSuiteMongoDbImage = "mongo@sha256:dc28ed5a9d0fc616903c3f463bb1e53b92b49f441721b91926e87d90bf957ce8";

    public const int DefaultSuitePort = 18443;

    public const int DefaultReceiverPort = 9444;

    public static string SuiteImage => Get(SuiteImageVariable) ?? DefaultSuiteImage;

    public static string SuiteNginxImage => Get(SuiteNginxImageVariable) ?? DefaultSuiteNginxImage;

    public static string SuiteMongoDbImage => Get(SuiteMongoDbImageVariable) ?? DefaultSuiteMongoDbImage;

    public static int SuitePort => Get(SuitePortVariable) is { } port ? int.Parse(port, System.Globalization.CultureInfo.InvariantCulture) : DefaultSuitePort;

    public static int ReceiverPort => Get(ReceiverPortVariable) is { } port ? int.Parse(port, System.Globalization.CultureInfo.InvariantCulture) : DefaultReceiverPort;

    public static Uri? ReceiverPushUrl => Get(ReceiverPushUrlVariable) is { } url ? new Uri(url) : null;

    private static string? Get(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
