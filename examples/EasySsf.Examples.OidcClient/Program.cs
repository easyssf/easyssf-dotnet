using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

using EasySsf.Receiver.AspNetCore;
using EasySsf.Receiver.Hosting;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;

// A web application that logs users in with Keycloak. The SSF receiver adds the push endpoint and
// rejects the authentication cookie of a user when Keycloak reports that their session was revoked
// or their credentials changed. Keycloak is both the identity provider and the SSF transmitter.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string issuer = builder.Configuration["KEYCLOAK_ISSUER"] ?? builder.Configuration["Keycloak:Issuer"] ?? "http://localhost:8080/realms/ssf-demo";
string clientId = builder.Configuration["Keycloak:ClientId"] ?? "example-oidc-client";
string clientSecret = builder.Configuration["KEYCLOAK_CLIENT_SECRET"] ?? builder.Configuration["Keycloak:ClientSecret"] ?? "example-oidc-client-secret";
string? pushUrl = builder.Configuration["SSF_PUSH_URL"];

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
    .AddCookie(options =>
    {
        // the session check of the page expects a 401 instead of a redirect to the login
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/auth"))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = issuer;
        options.RequireHttpsMetadata = issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.ResponseType = "code";
        options.Scope.Add("profile");
        options.Scope.Add("email");
        // the redirect URI the realm allows for the client, the same as the Java example uses
        options.CallbackPath = "/login/oauth2/code/keycloak";
        // the ID token is sent as id_token_hint with the logout, so Keycloak logs out without asking
        options.SaveTokens = true;
        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            // the post-logout redirect URI the realm allows for the client
            context.ProtocolMessage.PostLogoutRedirectUri = $"{context.Request.Scheme}://{context.Request.Host}/";
            return Task.CompletedTask;
        };
        // keep the claim names of the ID token (sub, sid, iss, iat), which the revocation check reads
        options.MapInboundClaims = false;
        options.ClaimActions.Remove("iss");
        options.ClaimActions.Remove("iat");
        options.TokenValidationParameters.NameClaimType = "preferred_username";
        // the default error page only says that the login failed; log the reason
        options.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
                .LogWarning(context.Failure, "Login with Keycloak failed: {Reason}", context.Failure?.Message);
            context.Response.Redirect("/?error=login");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();

// the receiver: options from EasySsf:Receiver of appsettings.json, issuer and client shared with OIDC
builder.AddSsfReceiver().Configure(options =>
{
    options.TransmitterIssuer = issuer;
    options.OAuth2.TokenUri = new Uri(issuer.TrimEnd('/') + "/protocol/openid-connect/token");
    options.OAuth2.ClientId = clientId;
    options.OAuth2.ClientSecret = clientSecret;
    if (!string.IsNullOrEmpty(pushUrl))
    {
        options.Push.DeliveryEndpointUrl = new Uri(pushUrl);
    }
});
// rejects the cookie of a revoked session or user when it is presented next
builder.Services.AddSsfSessionRevocation();

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapSsfPush();

app.MapGet("/", async (HttpContext context, IAntiforgery antiforgery) =>
{
    AuthenticateResult authentication = await context.AuthenticateAsync();
    ClaimsPrincipal user = context.User;
    AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
    string accountConsole = issuer.TrimEnd('/') + "/account";
    return Results.Content(Home.Page(user.FindFirstValue("preferred_username"), user.FindFirstValue("sub"), user.FindFirstValue("sid"),
        authentication.Properties?.IssuedUtc?.ToString("O"), accountConsole, tokens.FormFieldName, tokens.RequestToken), "text/html; charset=utf-8");
}).RequireAuthorization();

// Lets the page find out whether its session still exists. The check does not call Keycloak: the
// receiver remembers the revocation Keycloak reported and the cookie handler rejects the cookie, so
// the request either arrives authenticated (200) or not (401).
app.MapGet("/auth/check", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    JsonObject check = new()
    {
        ["user"] = context.User.FindFirstValue("preferred_username"),
        ["sid"] = context.User.FindFirstValue("sid"),
        ["checkedAt"] = DateTimeOffset.UtcNow.ToString("O"),
    };
    return Results.Content(check.ToJsonString(), "application/json");
    // challenged by the cookie scheme, which answers 401 here instead of redirecting to Keycloak
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme });

// logging out of the application also logs the user out of Keycloak (RP-initiated logout)
app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
}).RequireAuthorization();

app.Run();

/// <summary>
/// The one page of the application.
/// </summary>
internal static class Home
{
    public static string Page(string? username, string? subject, string? sid, string? issuedAt, string accountConsole, string csrfField, string? csrfToken)
    {
        HtmlEncoder html = HtmlEncoder.Default;
        return $$"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>SSF example OIDC client</title></head>
            <body>
            <h1>Hello {{html.Encode(username ?? "")}}</h1>
            <table>
            <tr><td>Subject (sub)</td><td><code>{{html.Encode(subject ?? "")}}</code></td></tr>
            <tr><td>Keycloak session (sid)</td><td><code>{{html.Encode(sid ?? "")}}</code></td></tr>
            <tr><td>Cookie issued at</td><td><code>{{html.Encode(issuedAt ?? "")}}</code></td></tr>
            </table>
            <p>Sign out in the <a href="{{html.Encode(accountConsole)}}" target="_blank">Keycloak account console</a>: Keycloak reports
            the revoked session with a security event and the cookie of this application is rejected from then on. This page
            checks its session every few seconds and whenever the tab becomes visible, and sends you to the login once the
            session has ended.</p>
            <p id="status">Session check: pending</p>
            <form method="post" action="/logout">
            <input type="hidden" name="{{html.Encode(csrfField)}}" value="{{html.Encode(csrfToken ?? "")}}">
            <button type="submit">Log out here (and in Keycloak)</button>
            </form>
            <script>
            (function () {
                const status = document.getElementById("status");
                let checks = 0;
                function checkSession() {
                    // GET /auth/check answers 200 as long as the cookie is accepted, 401 once the session was revoked.
                    // No call to Keycloak is needed: the receiver recorded the revocation when the event arrived.
                    fetch("/auth/check", { headers: { "Accept": "application/json" }, cache: "no-store",
                                           credentials: "same-origin", redirect: "manual" })
                        .then(function (response) {
                            if (response.status !== 200) {
                                status.textContent = "Session check: session ended, redirecting to the login";
                                window.location.reload();
                                return;
                            }
                            checks++;
                            status.textContent = "Session check " + checks + ": active at " + new Date().toLocaleTimeString();
                        })
                        .catch(function () {
                            status.textContent = "Session check: application not reachable";
                        });
                }
                document.addEventListener("visibilitychange", function () {
                    if (!document.hidden) {
                        checkSession();
                    }
                });
                setInterval(checkSession, 5000);
                checkSession();
            })();
            </script>
            </body>
            </html>
            """;
    }
}
