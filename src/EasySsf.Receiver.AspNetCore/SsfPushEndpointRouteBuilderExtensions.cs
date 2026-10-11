using EasySsf.Receiver.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.AspNetCore;

/// <summary>
/// Maps the push endpoint of the receiver.
/// </summary>
public static class SsfPushEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the endpoint the transmitter pushes SETs to (RFC 8935): <c>POST</c> at
    /// <see cref="SsfPushOptions.EndpointPath"/> (<c>/ssf/push</c> by default), open to anonymous
    /// requests, as a SET is authenticated by its signature and the optional <c>Authorization</c>
    /// header the <see cref="Push.SsfPushHandler"/> checks. Other methods on the path get a
    /// <c>405</c>. Requires <see cref="SsfReceiverServiceCollectionExtensions.AddSsfReceiver(IServiceCollection)"/>.
    /// </summary>
    /// <param name="endpoints">the endpoints of the application</param>
    /// <param name="pattern">the route pattern, <see cref="SsfPushOptions.EndpointPath"/> if <see langword="null"/></param>
    /// <returns>the endpoint, to add conventions such as a rate limiter</returns>
    public static IEndpointConventionBuilder MapSsfPush(this IEndpointRouteBuilder endpoints, string? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        SsfReceiverOptions options = endpoints.ServiceProvider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
        string path = pattern ?? options.Push.EndpointPath;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            throw new SsfReceiverConfigurationException(SsfReceiverDefaults.ConfigurationSection + ":Push:EndpointPath", path,
                "The path of the push endpoint must start with '/'");
        }
        endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(SsfPushEndpointRouteBuilderExtensions))
            .LogInformation("Receiving SSF security events at {Path}", path);
        return endpoints.MapPost(path, SsfPushEndpoint.HandleAsync).AllowAnonymous().WithDisplayName("SSF push endpoint");
    }
}
