using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Metadata;
using EasySsf.Core.Stream;
using EasySsf.Core.Support;
using EasySsf.Receiver.Transmitter;

namespace EasySsf.Receiver.Stream;

/// <summary>
/// Client for the stream management API of the transmitter (SSF 1.0, section 8.1). The endpoints
/// are discovered from the transmitter metadata.
/// </summary>
public sealed class SsfStreamClient
{
    private static readonly MediaTypeWithQualityHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _httpClient;

    private readonly ISsfTransmitterTokenProvider _tokenProvider;

    private readonly SsfTransmitterMetadataResolver _metadataResolver;

    /// <param name="httpClient">used to call the transmitter</param>
    /// <param name="tokenProvider">provides the access token to authenticate with</param>
    /// <param name="metadataResolver">resolves the endpoints of the transmitter</param>
    public SsfStreamClient(HttpClient httpClient, ISsfTransmitterTokenProvider tokenProvider, SsfTransmitterMetadataResolver metadataResolver)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(metadataResolver);
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _metadataResolver = metadataResolver;
    }

    /// <summary>
    /// The streams of this receiver.
    /// </summary>
    public async Task<IReadOnlyList<SsfStreamConfiguration>> GetStreamsAsync(CancellationToken cancellationToken = default)
    {
        Uri endpoint = await ConfigurationEndpointAsync(cancellationToken).ConfigureAwait(false);
        JsonElement? body = await ExchangeAsync(HttpMethod.Get, endpoint, null, cancellationToken).ConfigureAwait(false);
        List<SsfStreamConfiguration> streams = [];
        if (body is null)
        {
            return streams;
        }
        if (body.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement stream in body.Value.EnumerateArray())
            {
                if (stream.ValueKind == JsonValueKind.Object)
                {
                    streams.Add(new SsfStreamConfiguration(stream));
                }
            }
        }
        else if (body.Value.ValueKind == JsonValueKind.Object)
        {
            streams.Add(new SsfStreamConfiguration(body));
        }
        else
        {
            throw new SsfStreamException("The transmitter returned an invalid list of streams", 0, null);
        }
        return streams;
    }

    public async Task<SsfStreamConfiguration> GetStreamAsync(string streamId, CancellationToken cancellationToken = default)
    {
        Uri endpoint = WithStreamId(await ConfigurationEndpointAsync(cancellationToken).ConfigureAwait(false), streamId);
        return new SsfStreamConfiguration(await ExchangeObjectAsync(HttpMethod.Get, endpoint, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Creates a stream.
    /// </summary>
    /// <exception cref="SsfStreamIssuerMismatchException">
    /// if the transmitter created the stream with another issuer than its own, the stream must not
    /// be used then
    /// </exception>
    /// <seealso cref="SsfStreamConfiguration.Push"/>
    /// <seealso cref="SsfStreamConfiguration.Poll"/>
    public async Task<SsfStreamConfiguration> CreateStreamAsync(SsfStreamConfiguration stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Uri endpoint = await ConfigurationEndpointAsync(cancellationToken).ConfigureAwait(false);
        SsfStreamConfiguration created = new(await ExchangeObjectAsync(HttpMethod.Post, endpoint, stream.ToJsonObject(), cancellationToken).ConfigureAwait(false));
        string issuer = (await _metadataResolver.ResolveAsync(cancellationToken).ConfigureAwait(false)).Issuer;
        if (issuer != created.Issuer)
        {
            throw new SsfStreamIssuerMismatchException(issuer, created);
        }
        return created;
    }

    /// <summary>
    /// Updates the given members of a stream, the others are left as they are.
    /// </summary>
    /// <param name="streamId">the stream</param>
    /// <param name="changes">the members to change</param>
    /// <param name="cancellationToken">cancels the call</param>
    public async Task<SsfStreamConfiguration> UpdateStreamAsync(string streamId, JsonObject changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Uri endpoint = await ConfigurationEndpointAsync(cancellationToken).ConfigureAwait(false);
        return new SsfStreamConfiguration(await ExchangeObjectAsync(HttpMethod.Patch, endpoint, WithStreamId(changes, streamId), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Replaces the configuration of a stream.
    /// </summary>
    public async Task<SsfStreamConfiguration> ReplaceStreamAsync(string streamId, SsfStreamConfiguration stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Uri endpoint = await ConfigurationEndpointAsync(cancellationToken).ConfigureAwait(false);
        return new SsfStreamConfiguration(await ExchangeObjectAsync(HttpMethod.Put, endpoint, WithStreamId(stream.ToJsonObject(), streamId), cancellationToken).ConfigureAwait(false));
    }

    public async Task DeleteStreamAsync(string streamId, CancellationToken cancellationToken = default)
    {
        Uri endpoint = WithStreamId(await ConfigurationEndpointAsync(cancellationToken).ConfigureAwait(false), streamId);
        await ExchangeAsync(HttpMethod.Delete, endpoint, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SsfStreamStatus> GetStatusAsync(string streamId, CancellationToken cancellationToken = default)
    {
        Uri endpoint = WithStreamId(await StatusEndpointAsync(cancellationToken).ConfigureAwait(false), streamId);
        return ToStatus(await ExchangeObjectAsync(HttpMethod.Get, endpoint, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Enables, pauses or disables a stream.
    /// </summary>
    /// <param name="streamId">the stream</param>
    /// <param name="status">one of <see cref="SsfStreamStatus.Enabled"/>, <see cref="SsfStreamStatus.Paused"/> and <see cref="SsfStreamStatus.Disabled"/></param>
    /// <param name="reason">why the status is changed, may be <see langword="null"/></param>
    /// <param name="cancellationToken">cancels the call</param>
    public async Task<SsfStreamStatus> UpdateStatusAsync(string streamId, string status, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        JsonObject body = new() { ["status"] = status };
        if (reason is not null)
        {
            body["reason"] = reason;
        }
        Uri endpoint = await StatusEndpointAsync(cancellationToken).ConfigureAwait(false);
        return ToStatus(await ExchangeObjectAsync(HttpMethod.Post, endpoint, WithStreamId(body, streamId), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Asks the transmitter to send events about the given subject.
    /// </summary>
    /// <param name="streamId">the stream</param>
    /// <param name="subject">a subject identifier, see <see cref="Core.Event.SsfSubjectIdentifiers"/></param>
    /// <param name="verified">whether the receiver has verified the subject</param>
    /// <param name="cancellationToken">cancels the call</param>
    public async Task AddSubjectAsync(string streamId, JsonObject subject, bool verified, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        JsonObject body = new() { ["subject"] = subject.DeepClone(), ["verified"] = verified };
        Uri endpoint = await EndpointAsync(metadata => metadata.AddSubjectEndpoint, "add_subject_endpoint", cancellationToken).ConfigureAwait(false);
        await ExchangeAsync(HttpMethod.Post, endpoint, WithStreamId(body, streamId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the transmitter to no longer send events about the given subject.
    /// </summary>
    public async Task RemoveSubjectAsync(string streamId, JsonObject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        JsonObject body = new() { ["subject"] = subject.DeepClone() };
        Uri endpoint = await EndpointAsync(metadata => metadata.RemoveSubjectEndpoint, "remove_subject_endpoint", cancellationToken).ConfigureAwait(false);
        await ExchangeAsync(HttpMethod.Post, endpoint, WithStreamId(body, streamId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the transmitter to send a verification event on the stream.
    /// </summary>
    /// <param name="streamId">the stream</param>
    /// <param name="state">echoed in the verification event, may be <see langword="null"/></param>
    /// <param name="cancellationToken">cancels the call</param>
    public async Task RequestVerificationAsync(string streamId, string? state, CancellationToken cancellationToken = default)
    {
        JsonObject body = new();
        if (state is not null)
        {
            body["state"] = state;
        }
        Uri endpoint = await EndpointAsync(metadata => metadata.VerificationEndpoint, "verification_endpoint", cancellationToken).ConfigureAwait(false);
        await ExchangeAsync(HttpMethod.Post, endpoint, WithStreamId(body, streamId), cancellationToken).ConfigureAwait(false);
    }

    private Task<Uri> ConfigurationEndpointAsync(CancellationToken cancellationToken)
    {
        return EndpointAsync(metadata => metadata.ConfigurationEndpoint, "configuration_endpoint", cancellationToken);
    }

    private Task<Uri> StatusEndpointAsync(CancellationToken cancellationToken)
    {
        return EndpointAsync(metadata => metadata.StatusEndpoint, "status_endpoint", cancellationToken);
    }

    private async Task<Uri> EndpointAsync(Func<SsfTransmitterMetadata, Uri?> endpoint, string name, CancellationToken cancellationToken)
    {
        SsfTransmitterMetadata metadata;
        try
        {
            metadata = await _metadataResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SsfTransmitterUnavailableException ex)
        {
            throw new SsfStreamException(ex.Message, 0, ex);
        }
        return endpoint(metadata) ?? throw new SsfStreamException("The SSF transmitter metadata has no " + name, 0, null);
    }

    private async Task<JsonElement> ExchangeObjectAsync(HttpMethod method, Uri uri, JsonObject? body, CancellationToken cancellationToken)
    {
        JsonElement? response = await ExchangeAsync(method, uri, body, cancellationToken).ConfigureAwait(false);
        if (response is not { ValueKind: JsonValueKind.Object } result)
        {
            throw new SsfStreamException("The transmitter returned an invalid response", 0, null);
        }
        return result;
    }

    /// <returns>the parsed response body, <see langword="null"/> if the response had none</returns>
    private async Task<JsonElement?> ExchangeAsync(HttpMethod method, Uri uri, JsonObject? body, CancellationToken cancellationToken)
    {
        string? json = body?.ToJsonString();
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(method, uri, json, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // the access token expired or was revoked: try once more with a new one
                response.Dispose();
                _tokenProvider.Invalidate();
                response = await SendAsync(method, uri, json, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or SsfTransmitterUnavailableException or OperationCanceledException)
        {
            throw new SsfStreamException($"{method} {uri} failed: {ex.Message}", 0, ex);
        }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SsfStreamException($"{method} {uri} failed with status {(int)response.StatusCode}: {text}",
                    (int)response.StatusCode, null);
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return SsfJson.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new SsfStreamException("The transmitter returned an invalid response", 0, ex);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? json, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, uri);
        request.Headers.Accept.Add(JsonMediaType);
        string? accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static SsfStreamStatus ToStatus(JsonElement claims)
    {
        return new SsfStreamStatus(claims.StringMember("stream_id"), claims.StringMember("status"), claims.StringMember("reason"));
    }

    private static Uri WithStreamId(Uri endpoint, string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        string separator = string.IsNullOrEmpty(endpoint.Query) ? "?" : "&";
        return new Uri(endpoint + separator + "stream_id=" + Uri.EscapeDataString(streamId));
    }

    private static JsonObject WithStreamId(JsonObject body, string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        JsonObject withStreamId = new() { ["stream_id"] = streamId };
        foreach ((string name, JsonNode? value) in body)
        {
            if (name != "stream_id")
            {
                withStreamId[name] = value?.DeepClone();
            }
        }
        return withStreamId;
    }
}
