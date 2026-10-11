using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;

using EasySsf.Receiver.AspNetCore;
using EasySsf.Testing;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.AspNetCore.Tests;

/// <summary>
/// A resource server whose access tokens, and an OIDC client whose cookie sessions, end when the
/// identity provider revokes the session or changes a credential.
/// </summary>
public sealed class RevocationTests : IDisposable
{
    private readonly TestTransmitter _transmitter = new();

    public void Dispose()
    {
        _transmitter.Dispose();
    }

    [Fact]
    public async Task SessionRevokedEventInvalidatesAccessTokensOfThatSession()
    {
        await using ReceiverApp app = await ReceiverApp.StartAsync(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _transmitter.Issuer,
            ["EasySsf:Receiver:ExpectedAudience"] = TestTransmitter.Audience,
        }, configure: builder =>
        {
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = _transmitter.Issuer,
                    ValidateAudience = false,
                    IssuerSigningKey = new RsaSecurityKey(_transmitter.Key.Rsa.ExportParameters(false)),
                };
                options.MapInboundClaims = false;
            });
            builder.Services.AddSsfTokenRevocation();
            builder.Services.AddAuthorization();
        }, map: web =>
        {
            web.UseAuthentication();
            web.UseAuthorization();
            web.MapSsfPush();
            web.MapGet("/api/me", (ClaimsPrincipal user) => Results.Text(user.FindFirst("sub")!.Value)).RequireAuthorization();
        }, _transmitter);

        string aliceSession1 = _transmitter.AccessToken("alice", "session-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        string aliceSession2 = _transmitter.AccessToken("alice", "session-2", DateTimeOffset.UtcNow.AddMinutes(-1));
        string bob = _transmitter.AccessToken("bob", "session-3", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.OK, await ApiAsync(app, aliceSession1));
        Assert.Equal(HttpStatusCode.Unauthorized, await ApiAsync(app, null));

        string set = _transmitter.Set("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
        Assert.Equal(HttpStatusCode.Accepted, (await app.PushAsync(set)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await ApiAsync(app, aliceSession1));
        Assert.Equal(HttpStatusCode.OK, await ApiAsync(app, aliceSession2));
        Assert.Equal(HttpStatusCode.OK, await ApiAsync(app, bob));

        // an event that names only the user revokes the tokens issued up to the event
        string userSet = _transmitter.Set("CaepSessionRevoked", IssSub(_transmitter.Issuer, "alice"));
        Assert.Equal(HttpStatusCode.Accepted, (await app.PushAsync(userSet)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await ApiAsync(app, aliceSession2));
        Assert.Equal(HttpStatusCode.OK, await ApiAsync(app, _transmitter.AccessToken("alice", "session-4", DateTimeOffset.UtcNow.AddSeconds(5))));
        Assert.Equal(HttpStatusCode.OK, await ApiAsync(app, bob));
    }

    [Fact]
    public async Task RevokedCookieSessionIsSignedOutOnItsNextRequest()
    {
        await using ReceiverApp app = await ReceiverApp.StartAsync(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _transmitter.Issuer,
            ["EasySsf:Receiver:ExpectedAudience"] = TestTransmitter.Audience,
            ["EasySsf:Receiver:Revocation:UserEventTypes:0"] = "CaepCredentialChange",
        }, configure: builder =>
        {
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            {
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            });
            builder.Services.AddSsfSessionRevocation();
            builder.Services.AddAuthorization();
        }, map: web =>
        {
            web.UseAuthentication();
            web.UseAuthorization();
            web.MapSsfPush();
            web.MapGet("/login", async (HttpContext context, string sub, string sid) =>
            {
                ClaimsIdentity identity = new(
                [
                    new Claim("iss", _transmitter.Issuer), new Claim("sub", sub), new Claim("sid", sid),
                ], CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignInAsync(new ClaimsPrincipal(identity));
                return Results.Ok();
            });
            web.MapGet("/me", (ClaimsPrincipal user) => Results.Text(user.FindFirst("sub")!.Value)).RequireAuthorization();
        }, _transmitter);

        string alice1 = await LoginAsync(app, "alice", "session-1");
        string alice2 = await LoginAsync(app, "alice", "session-2");
        string bob = await LoginAsync(app, "bob", "session-3");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(app, alice1));

        string set = _transmitter.Set("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
        Assert.Equal(HttpStatusCode.Accepted, (await app.PushAsync(set)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, alice1));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(app, alice2));

        // a credential change ends every session of the user that started before it
        string changed = _transmitter.Set("CaepCredentialChange", IssSub(_transmitter.Issuer, "alice"));
        await Task.Delay(1100);
        Assert.Equal(HttpStatusCode.Accepted, (await app.PushAsync(changed)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, alice2));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(app, bob));
        await Task.Delay(1100);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(app, await LoginAsync(app, "alice", "session-5")));
    }

    private static async Task<HttpStatusCode> ApiAsync(ReceiverApp app, string? accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/me");
        if (accessToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }
        using HttpResponseMessage response = await app.Client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<string> LoginAsync(ReceiverApp app, string sub, string sid)
    {
        using HttpResponseMessage response = await app.Client.GetAsync($"/login?sub={sub}&sid={sid}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';')[0];
    }

    private static async Task<HttpStatusCode> MeAsync(ReceiverApp app, string cookie)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/me");
        request.Headers.Add("Cookie", cookie);
        using HttpResponseMessage response = await app.Client.SendAsync(request);
        return response.StatusCode;
    }
}
