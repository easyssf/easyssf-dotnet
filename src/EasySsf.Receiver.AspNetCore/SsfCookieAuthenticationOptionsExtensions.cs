using EasySsf.Receiver.Revocation;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EasySsf.Receiver.AspNetCore;

/// <summary>
/// Ends authentication cookie sessions that a security event revoked. Cookies are stateless, so the
/// check happens when a cookie is presented: a principal whose session (<c>sid</c>) was revoked, or
/// whose user (<c>sub</c> at <c>iss</c>) was revoked after the cookie was issued, is rejected and
/// signed out. For sessions kept server-side, an <c>ISsfSessionTerminator</c> ends them when the
/// event arrives instead.
/// </summary>
public static class SsfCookieAuthenticationOptionsExtensions
{
    /// <summary>
    /// Makes the cookie scheme consult the <see cref="ISsfTokenRevocationStore"/> whenever a cookie
    /// is validated, and reject and sign out a revoked principal. Keeps an <c>OnValidatePrincipal</c>
    /// handler that is already set; call it after that handler is assigned, or use
    /// <see cref="SsfAuthenticationServiceCollectionExtensions.AddSsfSessionRevocation"/>, which runs
    /// after the scheme is configured.
    /// </summary>
    public static CookieAuthenticationOptions UseSsfSessionRevocation(this CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Func<CookieValidatePrincipalContext, Task> previous = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            await previous(context).ConfigureAwait(false);
            if (context.Principal is null)
            {
                return;
            }
            // the cookie was issued when the user logged in: a revocation of the user after that
            // does not end the session
            DateTimeOffset? issuedAt = context.Properties.IssuedUtc ?? SsfPrincipalClaims.IssuedAt(context.Principal);
            if (await SsfJwtBearerOptionsExtensions.IsRevokedAsync(context.HttpContext.RequestServices, context.Principal, issuedAt,
                    context.HttpContext.RequestAborted).ConfigureAwait(false))
            {
                context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(SsfCookieAuthenticationOptionsExtensions))
                    .LogInformation("Ending the revoked session of subject {Subject}", SsfPrincipalClaims.Subject(context.Principal));
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
            }
        };
        return options;
    }
}
