using System.Text;

using EasySsf.Receiver.Push;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EasySsf.Receiver.AspNetCore;

/// <summary>
/// The endpoint that receives SETs delivered using HTTP push (RFC 8935) and hands them to the
/// <see cref="SsfPushHandler"/> of the application. Mapped with
/// <see cref="SsfPushEndpointRouteBuilderExtensions.MapSsfPush"/>.
/// </summary>
public static class SsfPushEndpoint
{
    /// <summary>
    /// Handles a push request: reads the SET from the body, lets the <see cref="SsfPushHandler"/>
    /// verify and handle it, and writes the status and error document of RFC 8935.
    /// </summary>
    public static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SsfPushHandler pushHandler = context.RequestServices.GetRequiredService<SsfPushHandler>();
        byte[] body = await ReadBodyAsync(context.Request.Body, SsfPushHandler.MaxSetSize + 1, context.RequestAborted).ConfigureAwait(false);
        string? authorization = context.Request.Headers.Authorization.FirstOrDefault();
        SsfPushResponse response = await pushHandler.HandleAsync(authorization, body, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = response.Status;
        if (response.Body is not null)
        {
            byte[] content = Encoding.UTF8.GetBytes(response.Body);
            context.Response.ContentType = SsfPushResponse.ContentType + "; charset=utf-8";
            context.Response.Headers.ContentLanguage = SsfPushResponse.ContentLanguage;
            context.Response.ContentLength = content.Length;
            await context.Response.Body.WriteAsync(content, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes: an endpoint does not need to read more than one
    /// byte beyond the maximum size of a SET to know that it is too large.
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(System.IO.Stream body, int limit, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[limit];
        int total = 0;
        while (total < limit)
        {
            int read = await body.ReadAsync(buffer.AsMemory(total, limit - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return (total == limit) ? buffer : buffer[..total];
    }
}
