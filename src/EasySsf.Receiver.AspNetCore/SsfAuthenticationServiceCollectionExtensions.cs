using EasySsf.Receiver.Hosting;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.AspNetCore;

/// <summary>
/// Hooks the revocation check of the receiver into the authentication schemes of the application.
/// </summary>
public static class SsfAuthenticationServiceCollectionExtensions
{
    /// <summary>
    /// Rejects revoked access tokens in the given JwtBearer scheme, see
    /// <see cref="SsfJwtBearerOptionsExtensions.UseSsfTokenRevocation"/>. Applied after the scheme's
    /// own configuration, so handlers the application set stay in place.
    /// </summary>
    /// <param name="services">the services of the application, with <c>AddSsfReceiver()</c></param>
    /// <param name="scheme">the scheme, <see cref="JwtBearerDefaults.AuthenticationScheme"/> if <see langword="null"/></param>
    public static IServiceCollection AddSsfTokenRevocation(this IServiceCollection services, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.PostConfigure<JwtBearerOptions>(scheme ?? JwtBearerDefaults.AuthenticationScheme, options => options.UseSsfTokenRevocation());
        return services;
    }

    /// <summary>
    /// Ends revoked sessions in the given cookie scheme, see
    /// <see cref="SsfCookieAuthenticationOptionsExtensions.UseSsfSessionRevocation"/>. Applied after
    /// the scheme's own configuration, so handlers the application set stay in place. The host
    /// fails to start when the cookie lives longer than <c>Revocation:Ttl</c> remembers a
    /// revocation (<c>ExpireTimeSpan</c>), because a revoked cookie would be accepted again once the
    /// revocation is forgotten; with sliding expiration the lifetime is unbounded, which is logged.
    /// </summary>
    /// <param name="services">the services of the application, with <c>AddSsfReceiver()</c></param>
    /// <param name="scheme">the scheme, <see cref="CookieAuthenticationDefaults.AuthenticationScheme"/> if <see langword="null"/></param>
    public static IServiceCollection AddSsfSessionRevocation(this IServiceCollection services, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        string schemeName = scheme ?? CookieAuthenticationDefaults.AuthenticationScheme;
        services.AddOptions<CookieAuthenticationOptions>(schemeName)
            .PostConfigure<IOptions<SsfReceiverOptions>, ILoggerFactory>((options, receiver, loggers) =>
            {
                options.UseSsfSessionRevocation();
                CheckCookieLifetime(schemeName, options, receiver.Value.Revocation, loggers.CreateLogger(typeof(SsfAuthenticationServiceCollectionExtensions)));
            })
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// A revocation has to outlive every cookie it may concern.
    /// </summary>
    /// <exception cref="SsfReceiverConfigurationException">if the cookie lives longer than a revocation is remembered</exception>
    internal static void CheckCookieLifetime(string scheme, CookieAuthenticationOptions options, SsfRevocationOptions revocation, ILogger logger)
    {
        if (!revocation.Enabled)
        {
            return;
        }
        string key = SsfReceiverDefaults.ConfigurationSection + ":Revocation:Ttl";
        if (options.ExpireTimeSpan > revocation.Ttl)
        {
            throw new SsfReceiverConfigurationException(key, revocation.Ttl.ToString(),
                $"The cookies of the scheme '{scheme}' live {options.ExpireTimeSpan} (ExpireTimeSpan), longer than a revocation is remembered;"
                    + $" set {key} to at least the cookie lifetime, or shorten the cookie");
        }
        if (options.SlidingExpiration)
        {
            logger.LogWarning("The cookies of the scheme '{Scheme}' use sliding expiration, so a cookie can outlive {Key} ({Ttl}) and a revoked session"
                + " be accepted again once the revocation is forgotten; a cookie renewed after the revocation is rejected, but one that is renewed"
                + " only later is not. Switch sliding expiration off, or set the TTL to the longest time a session may stay in use.", scheme, key, revocation.Ttl);
        }
    }
}
