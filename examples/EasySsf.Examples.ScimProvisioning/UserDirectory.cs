using System.Collections.Concurrent;

namespace EasySsf.Examples.ScimProvisioning;

/// <summary>
/// The local directory the SCIM Events are mirrored into, in memory. An application would keep its
/// own user table here.
/// </summary>
public sealed class UserDirectory
{
    private readonly ConcurrentDictionary<string, User> _users = new(StringComparer.Ordinal);

    public void Save(User user)
    {
        _users[user.Id] = user;
    }

    public User? Find(string id)
    {
        return _users.TryGetValue(id, out User? user) ? user : null;
    }

    /// <summary>
    /// Changes the user, if it is in the directory.
    /// </summary>
    /// <returns>the changed user, <see langword="null"/> if the directory has no such user</returns>
    public User? Update(string id, Func<User, User> change)
    {
        while (_users.TryGetValue(id, out User? current))
        {
            User changed = change(current);
            if (_users.TryUpdate(id, changed, current))
            {
                return changed;
            }
        }
        return null;
    }

    public User? Remove(string id)
    {
        return _users.TryRemove(id, out User? user) ? user : null;
    }

    public IReadOnlyList<User> All()
    {
        return _users.Values.OrderBy(user => user.UserName, StringComparer.Ordinal).ToList();
    }
}
