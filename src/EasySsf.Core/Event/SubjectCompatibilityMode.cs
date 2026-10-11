namespace EasySsf.Core.Event;

/// <summary>
/// Which convention for the subject of a SET a peer follows. SSF 1.0 requires a top-level
/// <c>sub_id</c> claim in every SET, including those of the CAEP and RISC event types defined
/// before it; earlier drafts put the subject into the event payload instead. A receiver
/// validates by it, a transmitter would emit by it.
/// </summary>
public enum SubjectCompatibilityMode
{
    /// <summary>
    /// SSF 1.0: a SET without a top-level <c>sub_id</c> is rejected as <c>invalid_request</c>.
    /// The default.
    /// </summary>
    Strict,

    /// <summary>
    /// The top-level <c>sub_id</c> is optional, for transmitters that follow earlier drafts;
    /// <c>SsfEventContext.SubjectFor</c> then reads the <c>subject</c> member of the event
    /// payload.
    /// </summary>
    Legacy,
}
