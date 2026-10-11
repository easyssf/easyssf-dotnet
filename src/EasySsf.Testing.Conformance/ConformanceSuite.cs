using System.Security.Cryptography.X509Certificates;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace EasySsf.Testing.Conformance;

/// <summary>
/// The OpenID conformance suite the tests run against, started with Testcontainers like
/// <c>docker-compose-prebuilt.yml</c> of the suite's repository does: MongoDB, the suite and its
/// nginx in front. The suite has to know its public URL before it starts, so nginx is bound to a
/// fixed host port and the suite's URL is <c>https://localhost:&lt;port&gt;</c>; the system under
/// test on the Docker host is reached from the containers as <c>host.docker.internal</c>, which
/// the containers get mapped to the host gateway. See <see cref="ConformanceSettings"/> for the
/// images and ports.
/// <para>
/// A singleton, as the test classes share it and discover their modules from it while the tests
/// are collected. The containers are removed by Testcontainers when the test process ends.
/// </para>
/// </summary>
public sealed class ConformanceSuite : IAsyncDisposable
{
    private const int SuitePort = 8080;

    private const int NginxPort = 8443;

    private const string NginxCertificatePath = "/etc/ssl/certs/nginx-selfsigned.crt";

    private static readonly Lazy<Task<ConformanceSuite>> Instance = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly INetwork _network;

    private readonly IReadOnlyList<IContainer> _containers;

    private readonly HttpClient _httpClient;

    private string? _certificateFile;

    private ConformanceSuite(Uri baseUri, X509Certificate2 certificate, INetwork network, IReadOnlyList<IContainer> containers)
    {
        BaseUri = baseUri;
        Certificate = certificate;
        _network = network;
        _containers = containers;
        _httpClient = ConformanceHttpClients.Create(certificate, false, TimeSpan.FromMinutes(2));
        Client = new ConformanceApiClient(baseUri, _httpClient);
    }

    /// <summary>
    /// The suite, started on first use and available.
    /// </summary>
    public static Task<ConformanceSuite> InstanceAsync()
    {
        return Instance.Value;
    }

    /// <summary>
    /// The URL under which the tests reach the suite, also the base of the issuers of its test
    /// instances (<c>&lt;base&gt;test/a/&lt;alias&gt;</c>).
    /// </summary>
    public Uri BaseUri { get; }

    /// <summary>
    /// The host under which the suite reaches the system under test.
    /// </summary>
    public string SystemUnderTestHost => "host.docker.internal";

    public ConformanceApiClient Client { get; }

    /// <summary>
    /// The TLS certificate of the suite.
    /// </summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>
    /// The TLS certificate of the suite as a PEM file, for a receiver under test that reads its
    /// trust from a file.
    /// </summary>
    public string CertificateFile
    {
        get
        {
            if (_certificateFile is null)
            {
                string file = Path.Combine(Path.GetTempPath(), "conformance-suite-" + Guid.NewGuid().ToString("N") + ".pem");
                File.WriteAllText(file, Certificate.ExportCertificatePem() + "\n");
                _certificateFile = file;
            }
            return _certificateFile;
        }
    }

    /// <summary>
    /// A new HTTP client for the receiver under test that trusts the suite's TLS certificate, for
    /// calling the transmitter the suite emulates.
    /// </summary>
    public HttpClient CreateHttpClient()
    {
        return ConformanceHttpClients.Create(Certificate, false, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The issuer of the test instance with the given alias.
    /// </summary>
    public string Issuer(string alias)
    {
        return new Uri(BaseUri, "test/a/" + alias).ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        foreach (IContainer container in _containers.Reverse())
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
        await _network.DisposeAsync().ConfigureAwait(false);
        if (_certificateFile is not null)
        {
            File.Delete(_certificateFile);
        }
    }

    private static async Task<ConformanceSuite> StartAsync()
    {
        int port = ConformanceSettings.SuitePort;
        Uri baseUri = new("https://localhost:" + port + "/");
        INetwork network = new NetworkBuilder().Build();
        IContainer mongodb = new ContainerBuilder(ConformanceSettings.SuiteMongoDbImage)
            .WithNetwork(network)
            .WithNetworkAliases("mongodb")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(27017, strategy => strategy.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        IContainer server = new ContainerBuilder(ConformanceSettings.SuiteImage)
            .WithNetwork(network)
            .WithNetworkAliases("server")
            .WithEnvironment("BASE_URL", baseUri.ToString().TrimEnd('/'))
            .WithEnvironment("BASE_MTLS_URL", baseUri.ToString().TrimEnd('/'))
            .WithEnvironment("MONGODB_HOST", "mongodb")
            // The suite pins MongoDB's feature compatibility version to the one it runs in production
            // (6.0) and refuses to start otherwise. The tests run a current MongoDB, so the suite is
            // told not to touch the version (an empty target skips the step).
            .WithEnvironment("OPENID_MONGODB_TARGETFEATURECOMPATIBILITYVERSION", "")
            // the 'dev' profile needs no login
            .WithEnvironment("SPRING_PROFILES_ACTIVE", "dev")
            .WithEnvironment("OIDC_GOOGLE_CLIENTID", "google-client")
            .WithEnvironment("OIDC_GOOGLE_SECRET", "google-secret")
            .WithEnvironment("OIDC_GITLAB_CLIENTID", "gitlab-client")
            .WithEnvironment("OIDC_GITLAB_SECRET", "gitlab-secret")
            // the suite delivers to the receiver under test on the Docker host
            .WithExtraHost("host.docker.internal", "host-gateway")
            .DependsOn(mongodb)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(SuitePort, strategy => strategy.WithTimeout(TimeSpan.FromMinutes(4))))
            .Build();
        IContainer nginx = new ContainerBuilder(ConformanceSettings.SuiteNginxImage)
            .WithNetwork(network)
            .WithNetworkAliases("nginx")
            .WithPortBinding(port, NginxPort)
            .DependsOn(server)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(NginxPort, strategy => strategy.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        IReadOnlyList<IContainer> containers = [mongodb, server, nginx];
        try
        {
            await nginx.StartAsync().ConfigureAwait(false);
            byte[] encoded = await nginx.ReadFileAsync(NginxCertificatePath).ConfigureAwait(false);
            ConformanceSuite suite = new(baseUri, ConformanceHttpClients.LoadCertificate(encoded), network, containers);
            await suite.Client.WaitUntilAvailableAsync(TimeSpan.FromMinutes(4)).ConfigureAwait(false);
            return suite;
        }
        catch (Exception)
        {
            foreach (IContainer container in containers.Reverse())
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }
            await network.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
