using System.Net;
using System.Security.Cryptography.X509Certificates;

using EasySsf.Testing.Conformance;
using EasySsf.Testing.Conformance.Receiver;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EasySsf.Conformance.Receiver;

/// <summary>
/// The receiver under test for the OpenID Foundation conformance suite. Each test run assembles a
/// fresh receiver from the easyssf receiver library, see <see cref="ConformanceRun"/>, and plays
/// the sequence of stream operations the test expects. Configured at <c>Cts</c>
/// (<see cref="ConformanceReceiverSettings"/>), see <c>appsettings.json</c>.
/// </summary>
public static class CtsApplication
{
    /// <summary>
    /// The configuration section of the settings.
    /// </summary>
    public const string ConfigurationSection = "Cts";

    /// <summary>
    /// The key of the HTTP client that calls the transmitter and the suite.
    /// </summary>
    public const string TransmitterHttpClient = "cts-transmitter";

    /// <summary>
    /// Where the application listens by default (<c>Cts:Server:Url</c>); the suite only accepts
    /// <c>https</c> push endpoints.
    /// </summary>
    public const string DefaultServerUrl = "https://localhost:9443";

    /// <summary>
    /// The TLS certificate and key the server uses by default (<c>Cts:Server:CertificateFile</c> and
    /// <c>Cts:Server:KeyFile</c>), created with mkcert, see the README.
    /// </summary>
    public const string DefaultServerCertificateFile = "certs/localhost.pem";

    public const string DefaultServerKeyFile = "certs/localhost-key.pem";

    /// <summary>
    /// Builds the application. Services the <paramref name="configure"/> callback registers win
    /// over the defaults: the tests replace the <see cref="ConformanceRunner"/> with one that calls
    /// a transmitter of theirs.
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        ConfigureServer(builder);
        builder.Services.AddOptions<ConformanceReceiverSettings>().BindConfiguration(ConfigurationSection);
        builder.Services.TryAddKeyedSingleton(TransmitterHttpClient, (provider, _) =>
        {
            ConformanceReceiverSettings.TransmitterSettings transmitter = provider.GetRequiredService<IOptions<ConformanceReceiverSettings>>().Value.Transmitter;
            X509Certificate2? trusted = null;
            if (!string.IsNullOrWhiteSpace(transmitter.CertificateFile) && File.Exists(transmitter.CertificateFile))
            {
                trusted = ConformanceHttpClients.LoadCertificate(File.ReadAllBytes(transmitter.CertificateFile));
            }
            return ConformanceHttpClients.Create(trusted, transmitter.TrustAllCertificates, TimeSpan.FromSeconds(30));
        });
        builder.Services.TryAddSingleton(provider => new ConformanceRunner(provider.GetRequiredService<IOptions<ConformanceReceiverSettings>>().Value,
            provider.GetRequiredKeyedService<HttpClient>(TransmitterHttpClient), provider.GetRequiredService<ILoggerFactory>()));
        builder.Services.TryAddSingleton(provider => new ConformanceSuiteModules(provider.GetRequiredService<IOptions<ConformanceReceiverSettings>>().Value,
            provider.GetRequiredKeyedService<HttpClient>(TransmitterHttpClient)));
        WebApplication app = builder.Build();
        app.MapCtsEndpoints();
        return app;
    }

    /// <summary>
    /// Listens where <c>Cts:Server:Url</c> says, with TLS from the configured PEM files for an
    /// <c>https</c> URL. Port 0 picks a free port, for tests.
    /// </summary>
    private static void ConfigureServer(WebApplicationBuilder builder)
    {
        IConfigurationSection server = builder.Configuration.GetSection(ConfigurationSection + ":Server");
        Uri url = new(server["Url"] ?? DefaultServerUrl);
        X509Certificate2? certificate = null;
        if (url.Scheme == Uri.UriSchemeHttps)
        {
            string certificateFile = server["CertificateFile"] ?? DefaultServerCertificateFile;
            string keyFile = server["KeyFile"] ?? DefaultServerKeyFile;
            if (!File.Exists(certificateFile) || !File.Exists(keyFile))
            {
                throw new InvalidOperationException($"The TLS certificate {certificateFile} or its key {keyFile} does not exist; create them with mkcert"
                    + " (see README.md) or listen on http with Cts:Server:Url");
            }
            certificate = ConformanceHttpClients.LoadCertificateWithKey(File.ReadAllText(certificateFile), File.ReadAllText(keyFile));
        }
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            Action<ListenOptions> listen = options =>
            {
                if (certificate is not null)
                {
                    options.UseHttps(certificate);
                }
            };
            if (url.Host is "0.0.0.0" or "*" or "+" or "[::]")
            {
                kestrel.ListenAnyIP(url.Port, listen);
            }
            else if (IPAddress.TryParse(url.Host, out IPAddress? address))
            {
                kestrel.Listen(address, url.Port, listen);
            }
            else
            {
                kestrel.ListenLocalhost(url.Port, listen);
            }
        });
    }
}
