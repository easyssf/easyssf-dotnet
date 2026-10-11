using System.Collections.ObjectModel;
using System.Text.Json;

namespace EasySsf.Core.Event;

/// <summary>
/// A verified Security Event Token (SET, RFC 8417) with the SSF profile claims.
/// </summary>
public sealed record SsfEventToken
{
    private static readonly ReadOnlyCollection<string> NoAudience = new([]);

    /// <param name="jti">unique identifier of the SET</param>
    /// <param name="iss">issuer of the SET (the transmitter)</param>
    /// <param name="iat">time the SET was issued</param>
    /// <param name="aud">audiences of the SET, empty if the claim was absent</param>
    /// <param name="events">event payloads keyed by event type URI</param>
    /// <param name="subjectId">the top-level <c>sub_id</c> claim, <see langword="null"/> if absent</param>
    /// <param name="txn">transaction identifier, <see langword="null"/> if absent</param>
    /// <param name="claims">all claims of the SET, a JSON object</param>
    public SsfEventToken(string jti, string iss, DateTimeOffset iat, IEnumerable<string>? aud,
            IEnumerable<KeyValuePair<string, JsonElement>> events, JsonElement? subjectId, string? txn,
            JsonElement claims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);
        ArgumentException.ThrowIfNullOrWhiteSpace(iss);
        ArgumentNullException.ThrowIfNull(events);
        if (claims.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("claims must be a JSON object", nameof(claims));
        }
        Jti = jti;
        Iss = iss;
        Iat = iat;
        Aud = (aud is null) ? NoAudience : new ReadOnlyCollection<string>([.. aud]);
        OrderedDictionary<string, JsonElement> copies = new(StringComparer.Ordinal);
        foreach ((string eventType, JsonElement payload) in events)
        {
            copies[eventType] = payload.Clone();
        }
        Events = new ReadOnlyDictionary<string, JsonElement>(copies);
        SubjectId = subjectId?.Clone();
        Txn = txn;
        Claims = claims.Clone();
    }

    /// <summary>
    /// Unique identifier of the SET.
    /// </summary>
    public string Jti { get; }

    /// <summary>
    /// Issuer of the SET (the transmitter).
    /// </summary>
    public string Iss { get; }

    /// <summary>
    /// Time the SET was issued.
    /// </summary>
    public DateTimeOffset Iat { get; }

    /// <summary>
    /// Audiences of the SET, empty if the claim was absent.
    /// </summary>
    public IReadOnlyList<string> Aud { get; }

    /// <summary>
    /// Event payloads keyed by event type URI, in the order of the SET.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Events { get; }

    /// <summary>
    /// The top-level <c>sub_id</c> claim, <see langword="null"/> if absent.
    /// </summary>
    public JsonElement? SubjectId { get; }

    /// <summary>
    /// Transaction identifier, <see langword="null"/> if absent.
    /// </summary>
    public string? Txn { get; }

    /// <summary>
    /// All claims of the SET, a JSON object.
    /// </summary>
    public JsonElement Claims { get; }

    /// <summary>
    /// The key that identifies this SET across deliveries: the issuer and the <c>jti</c>, which is
    /// only unique per issuer (RFC 8417, section 2.2). A handler that has side effects outside the
    /// receiver's stores uses it to make them idempotent, because a SET may be handled more than
    /// once: after a handler failed, or on another instance after the one that handled it died
    /// before recording the success.
    /// </summary>
    public string IdempotencyKey => Iss + "::" + Jti;

    public bool Equals(SsfEventToken? other)
    {
        return other is not null && Jti == other.Jti && Iss == other.Iss && Iat == other.Iat && Txn == other.Txn
            && Aud.SequenceEqual(other.Aud) && JsonElement.DeepEquals(Claims, other.Claims);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Jti, Iss);
    }

    public override string ToString()
    {
        return $"SET {IdempotencyKey} [{string.Join(", ", Events.Keys.Select(SsfEventTypes.AliasOf))}]";
    }
}
