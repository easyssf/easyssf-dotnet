using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Session;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Session;

public class SsfSessionTerminationEventHandlerTests
{
    private readonly List<SsfSubject> _terminated = [];

    private readonly SsfSessionTerminationEventHandler _handler;

    public SsfSessionTerminationEventHandlerTests()
    {
        _handler = new SsfSessionTerminationEventHandler(new RecordingTerminator(_terminated), ["CaepSessionRevoked"],
            ["CaepCredentialChange", "ScimProvDeactivate", "ScimProvDelete"]);
    }

    [Fact]
    public async Task SessionEventTerminatesTheSessionNamed()
    {
        await _handler.HandleAsync(Context(SsfEventTypes.CaepSessionRevoked, Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"))));
        SsfSubject subject = Assert.Single(_terminated);
        Assert.Equal("session-1", subject.SessionId);
    }

    [Fact]
    public async Task UserEventTerminatesAllSessionsOfTheUser()
    {
        await _handler.HandleAsync(Context(SsfEventTypes.CaepCredentialChange, Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"))));
        SsfSubject subject = Assert.Single(_terminated);
        Assert.Null(subject.SessionId);
        Assert.Equal("alice", subject.Subject);
    }

    [Fact]
    public async Task ScimDeactivateAndDeleteTerminateTheSessionsOfTheResource()
    {
        await _handler.HandleAsync(Context(SsfEventTypes.ScimProvDeactivate, SsfSubjectIdentifiers.Scim("/Users/2b2f880a", "alice")));
        await _handler.HandleAsync(Context(SsfEventTypes.ScimProvDelete, SsfSubjectIdentifiers.Scim("/Users/2b2f880a")));
        await _handler.HandleAsync(Context(SsfEventTypes.ScimProvActivate, SsfSubjectIdentifiers.Scim("/Users/2b2f880a")));
        Assert.Equal(2, _terminated.Count);
        Assert.All(_terminated, subject => Assert.True(subject.UserIdentifier!.IsScim));
    }

    [Fact]
    public async Task EventWithoutSubjectTerminatesNothing()
    {
        await _handler.HandleAsync(Context(SsfEventTypes.ScimProvDeactivate, null));
        Assert.Empty(_terminated);
    }

    private static SsfEventContext Context(string eventType, JsonObject? subjectId)
    {
        List<KeyValuePair<string, JsonElement>> events = [new(eventType, SsfJson.EmptyObject)];
        JsonElement? subject = (subjectId is not null) ? SsfJson.ToElement(subjectId) : null;
        return new SsfEventContext(new SsfEventToken("jti-1", "https://idp.example", DateTimeOffset.Parse("2026-10-04T10:00:00Z"), null, events,
            subject, null, SsfJson.EmptyObject));
    }

    private sealed class RecordingTerminator(List<SsfSubject> terminated) : ISsfSessionTerminator
    {
        public ValueTask<int> TerminateSessionsAsync(SsfSubject subject, CancellationToken cancellationToken = default)
        {
            terminated.Add(subject);
            return ValueTask.FromResult(1);
        }
    }
}
