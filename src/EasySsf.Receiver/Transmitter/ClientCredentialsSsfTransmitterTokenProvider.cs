using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using EasySsf.Core.Support;

namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// <see cref="ISsfTransmitterTokenProvider"/> that obtains access tokens with the OAuth2 client
/// credentials grant (RFC 6749, section 4.4) and caches them until shortly before they expire.
/// </summary>
public sealed class ClientCredentialsSsfTransmitterTokenProvider : ISsfTransmitterTokenProvider, IDisposable
{
    private static readonly MediaTypeWithQualityHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _httpClient;

    private readonly Uri _tokenUri;

    private readonly string _clientId;

    private readonly string _clientSecret;

    private readonly SsfClientCredentialsOptions _options;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _accessToken;

    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <param name="httpClient">calls the token endpoint</param>
    /// <param name="tokenUri">the token endpoint of the authorization server of the transmitter</param>
    /// <param name="clientId">the client identifier of the receiver</param>
    /// <param name="clientSecret">the client secret of the receiver</param>
    /// <param name="options">scopes, authentication method and expiry handling, the defaults if <see langword="null"/></param>
    public ClientCredentialsSsfTransmitterTokenProvider(HttpClient httpClient, Uri tokenUri, string clientId, string clientSecret,
            SsfClientCredentialsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        _httpClient = httpClient;
        _tokenUri = tokenUri;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _options = options ?? new SsfClientCredentialsOptions();
        if (_options.ExpirySafetyWindow < TimeSpan.Zero)
        {
            throw new ArgumentException("ExpirySafetyWindow must not be negative", nameof(options));
        }
    }

    /// <summary>
    /// How tokens are obtained.
    /// </summary>
    public SsfClientCredentialsOptions Options => _options;

    public void Invalidate()
    {
        _lock.Wait();
        try
        {
            _accessToken = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is null || _options.TimeProvider.GetUtcNow() >= _expiresAt)
            {
                await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            return _accessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RequestTokenAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> form = [new("grant_type", "client_credentials")];
        if (_options.Scopes.Count > 0)
        {
            form.Add(new KeyValuePair<string, string>("scope", string.Join(' ', _options.Scopes)));
        }
        foreach ((string name, string value) in _options.AdditionalParameters)
        {
            if (!form.Any(parameter => parameter.Key == name))
            {
                form.Add(new KeyValuePair<string, string>(name, value));
            }
        }
        using HttpRequestMessage request = new(HttpMethod.Post, _tokenUri);
        request.Headers.Accept.Add(JsonMediaType);
        if (_options.AuthenticateWithRequestBody)
        {
            form.Add(new KeyValuePair<string, string>("client_id", _clientId));
            form.Add(new KeyValuePair<string, string>("client_secret", _clientSecret));
        }
        else
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(_clientId + ":" + _clientSecret));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        request.Content = new FormUrlEncodedContent(form);
        JsonElement response;
        try
        {
            using HttpResponseMessage httpResponse = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("The token endpoint answered with status " + (int)httpResponse.StatusCode);
            }
            response = SsfJson.Parse(await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SsfTransmitterUnavailableException(
                "Could not obtain an access token for the SSF transmitter from " + _tokenUri, ex);
        }
        string? token = response.StringMember("access_token");
        if (token is null)
        {
            throw new SsfTransmitterUnavailableException("The token response of " + _tokenUri + " has no access_token");
        }
        long expiresIn = (response.Member("expires_in") is { ValueKind: JsonValueKind.Number } seconds && seconds.TryGetInt64(out long lifetime))
            ? lifetime : 60;
        _accessToken = token;
        // renew ahead of the expiry, but still use a short-lived token for most of its lifetime
        long safetyWindow = Math.Min((long)_options.ExpirySafetyWindow.TotalSeconds, expiresIn / 4);
        _expiresAt = _options.TimeProvider.GetUtcNow().AddSeconds(expiresIn - safetyWindow);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
