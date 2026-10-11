using System.Text.Json;

using EasySsf.Core.Support;

namespace EasySsf.Core.Metadata;

/// <summary>
/// The transmitter configuration metadata published at <c>.well-known/ssf-configuration</c> (SSF
/// 1.0, section 7.1).
/// </summary>
public sealed record SsfTransmitterMetadata
{
    /// <summary>
    /// The <c>spec_version</c> of the final SSF 1.0 specification.
    /// </summary>
    public const string SpecVersion10 = "1_0";

    /// <param name="issuer">the transmitter issuer</param>
    /// <param name="jwksUri">location of the transmitter's JWK Set, may be <see langword="null"/></param>
    /// <param name="claims">all members of the metadata document, an empty object if <see langword="null"/></param>
    public SsfTransmitterMetadata(string issuer, Uri? jwksUri, JsonElement? claims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        if (claims is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new ArgumentException("claims must be a JSON object", nameof(claims));
        }
        Issuer = issuer;
        JwksUri = jwksUri;
        Claims = (claims is { ValueKind: JsonValueKind.Object } value) ? value.Clone() : SsfJson.EmptyObject;
    }

    /// <summary>
    /// The transmitter issuer.
    /// </summary>
    public string Issuer { get; }

    /// <summary>
    /// Location of the transmitter's JWK Set, may be <see langword="null"/>.
    /// </summary>
    public Uri? JwksUri { get; }

    /// <summary>
    /// All members of the metadata document, a JSON object.
    /// </summary>
    public JsonElement Claims { get; }

    /// <summary>
    /// The version of the specification the transmitter implements (<c>spec_version</c>),
    /// <see langword="null"/> if the transmitter does not say.
    /// </summary>
    public string? SpecVersion => Claims.StringMember("spec_version");

    /// <summary>
    /// The delivery method URIs the transmitter supports (<c>delivery_methods_supported</c>), empty
    /// if the transmitter does not say.
    /// </summary>
    public IReadOnlyList<string> DeliveryMethodsSupported => Claims.StringArrayMember("delivery_methods_supported");

    /// <summary>
    /// Whether the transmitter supports the delivery method; <see langword="true"/> as well if it
    /// does not say which methods it supports.
    /// </summary>
    public bool SupportsDeliveryMethod(SsfDeliveryMethod method)
    {
        IReadOnlyList<string> supported = DeliveryMethodsSupported;
        return supported.Count == 0 || supported.Contains(method.Uri());
    }

    /// <summary>
    /// The members of a complex subject the transmitter requires to be understood
    /// (<c>critical_subject_members</c>), empty if none.
    /// </summary>
    public IReadOnlyList<string> CriticalSubjectMembers => Claims.StringArrayMember("critical_subject_members");

    /// <summary>
    /// The authorization schemes of the management endpoints (<c>authorization_schemes</c>), each a
    /// JSON object with a <c>spec_urn</c>, empty if the transmitter does not say.
    /// </summary>
    public IReadOnlyList<JsonElement> AuthorizationSchemes
    {
        get
        {
            if (Claims.Member("authorization_schemes") is not { ValueKind: JsonValueKind.Array } schemes)
            {
                return [];
            }
            return schemes.EnumerateArray().Where(scheme => scheme.ValueKind == JsonValueKind.Object).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Which subjects a stream receives events for by default (<c>default_subjects</c>): <c>ALL</c>
    /// or <c>NONE</c>, <see langword="null"/> if the transmitter does not say.
    /// </summary>
    public string? DefaultSubjects => Claims.StringMember("default_subjects");

    /// <summary>
    /// Endpoint to create, read, update and delete streams, may be <see langword="null"/>.
    /// </summary>
    public Uri? ConfigurationEndpoint => Endpoint("configuration_endpoint");

    /// <summary>
    /// Endpoint to read and update the status of a stream, may be <see langword="null"/>.
    /// </summary>
    public Uri? StatusEndpoint => Endpoint("status_endpoint");

    /// <summary>
    /// Endpoint to add subjects to a stream, may be <see langword="null"/>.
    /// </summary>
    public Uri? AddSubjectEndpoint => Endpoint("add_subject_endpoint");

    /// <summary>
    /// Endpoint to remove subjects from a stream, may be <see langword="null"/>.
    /// </summary>
    public Uri? RemoveSubjectEndpoint => Endpoint("remove_subject_endpoint");

    /// <summary>
    /// Endpoint to request a verification event, may be <see langword="null"/>.
    /// </summary>
    public Uri? VerificationEndpoint => Endpoint("verification_endpoint");

    /// <exception cref="UriFormatException">if the member is not a URI</exception>
    private Uri? Endpoint(string name)
    {
        string? value = Claims.StringMember(name);
        return (value is not null) ? new Uri(value, UriKind.RelativeOrAbsolute) : null;
    }

    public bool Equals(SsfTransmitterMetadata? other)
    {
        return other is not null && Issuer == other.Issuer && JwksUri == other.JwksUri && JsonElement.DeepEquals(Claims, other.Claims);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Issuer, JwksUri);
    }

    public override string ToString()
    {
        return Claims.GetRawText();
    }
}
