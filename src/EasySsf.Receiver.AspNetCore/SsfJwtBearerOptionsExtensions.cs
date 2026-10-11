using System.Security.Claims;

using EasySsf.Receiver.Revocation;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EasySsf.Receiver.AspNetCore;

/// <summary>
/// Rejects access tokens that were revoked by a security event: tokens bound to a revoked session
/// (<c>sid</c> claim) and tokens of a revoked subject (<c>sub</c> claim) that were issued at or
/// before the revocation.
/// </summary>
public static class SsfJwtBearerOptionsExtensions
{
    /// <summary>
    /// Makes the JwtBearer scheme consult the <see cref="ISsfTokenRevocationStore"/> after a token
    /// was validated, and fail the authentication of a revoked token. Keeps an
    /// <c>OnTokenValidated</c> handler that is already set; call it after that handler is assigned,
    /// or use <see cref="SsfAuthenticationServiceCollectionExtensions.AddSsfTokenRevocation"/>, which
    /// runs after the scheme is configured.
    /// </summary>
    public static JwtBearerOptions UseSsfTokenRevocation(this JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Events ??= new JwtBearerEvents();
        Func<TokenValidatedContext, Task> previous = options.Events.OnTokenValidated;
        options.Events.OnTokenValidated = async context =>
        {
            await previous(context).ConfigureAwait(false);
            if (context.Result is not null || context.Principal is null)
            {
                return;
            }
            if (await IsRevokedAsync(context.HttpContext.RequestServices, context.Principal, SsfPrincipalClaims.IssuedAt(context.Principal),
                    context.HttpContext.RequestAborted).ConfigureAwait(false))
            {
                context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(SsfJwtBearerOptionsExtensions))
                    .LogDebug("Rejecting a revoked access token of subject {Subject}", SsfPrincipalClaims.Subject(context.Principal));
                context.Fail("The token was revoked");
            }
        };
        return options;
    }

    internal static async ValueTask<bool> IsRevokedAsync(IServiceProvider services, ClaimsPrincipal principal, DateTimeOffset? issuedAt,
            CancellationToken cancellationToken)
    {
        ISsfTokenRevocationStore store = services.GetRequiredService<ISsfTokenRevocationStore>();
        return await store.IsRevokedAsync(SsfPrincipalClaims.Issuer(principal), SsfPrincipalClaims.SessionId(principal),
            SsfPrincipalClaims.Subject(principal), issuedAt, cancellationToken).ConfigureAwait(false);
    }
}
