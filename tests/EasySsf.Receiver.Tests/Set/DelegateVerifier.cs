using EasySsf.Core.Event;
using EasySsf.Receiver.Set;

namespace EasySsf.Receiver.Tests.Set;

/// <summary>
/// A verifier for tests of what happens after verification: builds the token from a delegate.
/// </summary>
public sealed class DelegateVerifier(Func<string, SsfEventToken> verify) : ISsfSetVerifier
{
    public SsfEventToken Verify(string encodedSet)
    {
        return verify(encodedSet);
    }

    public Task<SsfEventToken> VerifyAsync(string encodedSet, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(verify(encodedSet));
    }
}
