using System.Reflection;

namespace EasySsf.Testing.Conformance;

/// <summary>
/// The files embedded in this assembly: the suite configurations and the TLS material of the
/// receiver under test.
/// </summary>
public static class ConformanceResources
{
    /// <summary>
    /// The TLS certificate of the receiver under test, issued by <see cref="TestCa"/> for
    /// <c>localhost</c>, <c>host.docker.internal</c> and <c>host.testcontainers.internal</c>.
    /// </summary>
    public const string ReceiverCertificate = "tls/receiver.pem";

    /// <summary>
    /// The TLS private key of the receiver under test.
    /// </summary>
    public const string ReceiverPrivateKey = "tls/receiver-key.pem";

    /// <summary>
    /// The CA that issued the certificate of the receiver under test.
    /// </summary>
    public const string TestCa = "test-ca.pem";

    /// <summary>
    /// Reads an embedded file as text.
    /// </summary>
    public static string Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Assembly assembly = typeof(ConformanceResources).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("No resource " + name + " in " + assembly.GetName().Name);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
