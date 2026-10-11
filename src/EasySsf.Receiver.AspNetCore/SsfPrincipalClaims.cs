using System.Globalization;
using System.Security.Claims;

using EasySsf.Receiver.Session;

namespace EasySsf.Receiver.AspNetCore;

/// <summary>
/// Reads the claims the revocation check needs from an authenticated principal, by JWT name or by
/// the claim type ASP.NET Core maps it to.
/// </summary>
internal static class SsfPrincipalClaims
{
    public static string? Issuer(ClaimsPrincipal principal)
    {
        return SsfSubjectClaimsMatcher.ClaimValue(principal, "iss");
    }

    public static string? SessionId(ClaimsPrincipal principal)
    {
        return SsfSubjectClaimsMatcher.ClaimValue(principal, "sid");
    }

    public static string? Subject(ClaimsPrincipal principal)
    {
        return SsfSubjectClaimsMatcher.ClaimValue(principal, "sub");
    }

    /// <summary>
    /// The <c>iat</c> claim as an instant, <see langword="null"/> if absent or not a number.
    /// </summary>
    public static DateTimeOffset? IssuedAt(ClaimsPrincipal principal)
    {
        string? iat = SsfSubjectClaimsMatcher.ClaimValue(principal, "iat");
        return (iat is not null && long.TryParse(iat, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }
}
