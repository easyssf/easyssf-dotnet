using EasySsf.Receiver.Event;
using EasySsf.Receiver.Set;

namespace EasySsf.Receiver.Stream;

/// <summary>
/// Validates stream verification events (SSF 1.0, section 8.1.4.1) before the handlers see them:
/// that the event is about the stream of this receiver and echoes a state the receiver sent.
/// Implemented by the stream management of the receiver, hooked into the
/// <see cref="SsfSetProcessor"/> through <see cref="SsfSetProcessorOptions.StreamVerifications"/>.
/// </summary>
public interface ISsfStreamVerification
{
    /// <summary>
    /// Validates the verification event of a SET, if it has one.
    /// </summary>
    /// <exception cref="SsfSetVerificationException">
    /// if the event is about another stream or echoes a state that is not expected
    /// </exception>
    void Validate(SsfEventContext eventContext);
}
