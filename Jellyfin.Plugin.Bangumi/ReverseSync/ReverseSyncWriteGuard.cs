using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Bangumi.ReverseSync;

public sealed class ReverseSyncWriteGuard
{
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), byte> _entries = new();

    public IDisposable Suppress(Guid userId, Guid itemId)
    {
        _entries[(userId, itemId)] = 0;
        return new Scope(_entries, (userId, itemId));
    }

    public bool IsSuppressed(Guid userId, Guid itemId) => _entries.ContainsKey((userId, itemId));

    private sealed class Scope(
        ConcurrentDictionary<(Guid UserId, Guid ItemId), byte> entries,
        (Guid UserId, Guid ItemId) key) : IDisposable
    {
        public void Dispose() => entries.TryRemove(key, out _);
    }
}
