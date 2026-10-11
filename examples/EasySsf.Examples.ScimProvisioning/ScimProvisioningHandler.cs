using System.Text.Json;

using EasySsf.Core.Event;
using EasySsf.Core.Scim;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Scim;

namespace EasySsf.Examples.ScimProvisioning;

/// <summary>
/// Mirrors the SCIM Events about <c>Users</c> into the <see cref="UserDirectory"/>. The receiver
/// calls it for every verified SET; <see cref="SsfScimEventHandler"/> dispatches the SCIM Events of
/// the SET to the method of their operation.
/// <para>
/// <c>full</c> events carry the resource (or the patch) as <c>data</c> and are applied directly.
/// <c>notice</c> events only name the attributes that changed: the application would fetch the
/// resource from the SCIM service provider with a GET of the subject's <c>uri</c>, which this
/// example does not have, so it logs them. Like every handler it is idempotent: applying an event
/// twice leaves the directory in the same state.
/// </para>
/// </summary>
public sealed class ScimProvisioningHandler(UserDirectory directory, ILogger<ScimProvisioningHandler> logger) : SsfScimEventHandler
{
    protected override Task OnScimEventAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        SsfScimSubject? subject = scimEvent.Subject;
        if (subject?.Id is null)
        {
            logger.LogWarning("{Event} of SET {Jti} has no SCIM subject, ignored", Alias(scimEvent), eventContext.EventToken.Jti);
            return Task.CompletedTask;
        }
        if (subject.ResourceType != "Users")
        {
            logger.LogInformation("{Event} for {Uri}: only Users are mirrored", Alias(scimEvent), subject.Uri);
            return Task.CompletedTask;
        }
        return base.OnScimEventAsync(scimEvent, eventContext, cancellationToken);
    }

    protected override Task OnFeedAddAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Event}: {Uri} joined the feed", Alias(scimEvent), scimEvent.Subject!.Uri);
        return Task.CompletedTask;
    }

    protected override Task OnFeedRemoveAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Event}: {Uri} left the feed, the user stays in the directory", Alias(scimEvent), scimEvent.Subject!.Uri);
        return Task.CompletedTask;
    }

    protected override Task OnCreateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        if (scimEvent.IsFull && scimEvent.Data is { } data)
        {
            User user = User.FromScim(scimEvent.Subject!.Id!, scimEvent.Subject.ExternalId, data, scimEvent.Version);
            directory.Save(user);
            logger.LogInformation("{Event}: created {User}", Alias(scimEvent), user);
        }
        else
        {
            LogNotice(scimEvent, "created");
        }
        return Task.CompletedTask;
    }

    protected override Task OnPutAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        if (scimEvent.IsFull && scimEvent.Data is { } data)
        {
            User user = User.FromScim(scimEvent.Subject!.Id!, scimEvent.Subject.ExternalId, data, scimEvent.Version);
            directory.Save(user);
            logger.LogInformation("{Event}: replaced {User}", Alias(scimEvent), user);
        }
        else
        {
            LogNotice(scimEvent, "replaced");
        }
        return Task.CompletedTask;
    }

    protected override Task OnPatchAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        if (!scimEvent.IsFull || scimEvent.Data is not { } data)
        {
            LogNotice(scimEvent, "modified");
            return Task.CompletedTask;
        }
        User? patched = directory.Update(scimEvent.Subject!.Id!, user => ApplyPatch(user, data).WithVersion(scimEvent.Version));
        if (patched is not null)
        {
            logger.LogInformation("{Event}: patched {User}", Alias(scimEvent), patched);
        }
        else
        {
            logger.LogWarning("{Event}: {Uri} is not in the directory, nothing to patch", Alias(scimEvent), scimEvent.Subject.Uri);
        }
        return Task.CompletedTask;
    }

    protected override Task OnDeleteAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        User? removed = directory.Remove(scimEvent.Subject!.Id!);
        logger.LogInformation("{Event}: deleted {User}", Alias(scimEvent), (removed is not null) ? removed.ToString() : scimEvent.Subject.Uri);
        return Task.CompletedTask;
    }

    protected override Task OnActivateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        SetActive(scimEvent, true);
        return Task.CompletedTask;
    }

    protected override Task OnDeactivateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        SetActive(scimEvent, false);
        return Task.CompletedTask;
    }

    private void SetActive(SsfScimEvent scimEvent, bool active)
    {
        User? user = directory.Update(scimEvent.Subject!.Id!, existing => existing.WithActive(active).WithVersion(scimEvent.Version));
        if (user is not null)
        {
            logger.LogInformation("{Event}: {What} {User}", Alias(scimEvent), active ? "activated" : "deactivated", user);
        }
        else
        {
            logger.LogWarning("{Event}: {Uri} is not in the directory", Alias(scimEvent), scimEvent.Subject.Uri);
        }
    }

    /// <summary>
    /// Applies the <c>Operations</c> of a SCIM <c>PatchOp</c> (RFC 7644, section 3.5.2) to the user,
    /// as far as this application mirrors the attributes: <c>replace</c> and <c>add</c> of a
    /// top-level attribute by <c>path</c>, or of several attributes with a <c>value</c> object and
    /// no path; <c>remove</c> of <c>emails</c>.
    /// </summary>
    private User ApplyPatch(User user, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object || !patch.TryGetProperty("Operations", out JsonElement operations) || operations.ValueKind != JsonValueKind.Array)
        {
            return user;
        }
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            if (operation.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string op = (operation.TryGetProperty("op", out JsonElement opValue) ? opValue.ToString() : "").ToLowerInvariant();
            string? path = (operation.TryGetProperty("path", out JsonElement pathValue) && pathValue.ValueKind == JsonValueKind.String) ? pathValue.GetString() : null;
            JsonElement? value = operation.TryGetProperty("value", out JsonElement v) ? v : null;
            switch (op)
            {
                case "add":
                case "replace":
                    if (path is not null && value is { } patched)
                    {
                        user = user.With(path, patched);
                    }
                    else if (value is { ValueKind: JsonValueKind.Object } attributes)
                    {
                        foreach (JsonProperty attribute in attributes.EnumerateObject())
                        {
                            user = user.With(attribute.Name, attribute.Value);
                        }
                    }
                    break;
                case "remove":
                    if (path == "emails")
                    {
                        user = user with { Emails = [] };
                    }
                    else
                    {
                        logger.LogDebug("remove of {Path} is not mirrored", path);
                    }
                    break;
                default:
                    logger.LogDebug("unknown PatchOp operation {Op}", op);
                    break;
            }
        }
        return user;
    }

    private void LogNotice(SsfScimEvent scimEvent, string what)
    {
        logger.LogInformation("{Event}: {Uri} {What} changed [{Attributes}]; a notice event has no data, the application would GET the resource from the SCIM service provider",
            Alias(scimEvent), scimEvent.Subject!.Uri, what, string.Join(", ", scimEvent.Attributes));
    }

    private static string Alias(SsfScimEvent scimEvent)
    {
        return SsfEventTypes.AliasOf(scimEvent.EventType);
    }
}
