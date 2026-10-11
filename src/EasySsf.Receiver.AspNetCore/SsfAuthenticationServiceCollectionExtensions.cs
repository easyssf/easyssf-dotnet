using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;

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
    /// the scheme's own configuration, so handlers the application set stay in place.
    /// </summary>
    /// <param name="services">the services of the application, with <c>AddSsfReceiver()</c></param>
    /// <param name="scheme">the scheme, <see cref="CookieAuthenticationDefaults.AuthenticationScheme"/> if <see langword="null"/></param>
    public static IServiceCollection AddSsfSessionRevocation(this IServiceCollection services, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.PostConfigure<CookieAuthenticationOptions>(scheme ?? CookieAuthenticationDefaults.AuthenticationScheme,
            options => options.UseSsfSessionRevocation());
        return services;
    }
}
