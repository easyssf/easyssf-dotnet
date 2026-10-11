using EasySsf.Receiver.Transmitter;

namespace EasySsf.Receiver.Hosting;

/// <summary>
/// Callback to customize a transmitter before it is built: replace a part the factory built from
/// the options, or add one. Every registered customizer is called for every transmitter, the
/// default one included; <see cref="SsfTransmitterBuilder.Name"/> tells which.
/// </summary>
public interface ISsfTransmitterCustomizer
{
    void Customize(SsfTransmitterBuilder builder);
}
