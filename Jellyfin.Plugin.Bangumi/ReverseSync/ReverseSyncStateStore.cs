using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Bangumi.ReverseSync;

internal sealed class ReverseSyncState
{
    public Dictionary<string, ReverseSyncUserState> Users { get; set; } = new();
}

internal sealed class ReverseSyncUserState
{
    public DateTime? LastSuccessfulSyncUtc { get; set; }

    public DateTime? LastFullSyncUtc { get; set; }

    public Dictionary<int, string> SubjectSnapshots { get; set; } = new();

    public Dictionary<int, string> LocalEpisodeSnapshots { get; set; } = new();
}

public sealed class ReverseSyncStateStore(IApplicationPaths applicationPaths)
{
    private string StatePath => Path.Join(
        applicationPaths.PluginConfigurationsPath,
        "Jellyfin.Plugin.Bangumi.ReverseSync.json");

    internal ReverseSyncState Load()
    {
        if (!File.Exists(StatePath))
            return new ReverseSyncState();

        try
        {
            return JsonSerializer.Deserialize<ReverseSyncState>(File.ReadAllText(StatePath), Constants.JsonSerializerOptions)
                ?? new ReverseSyncState();
        }
        catch (JsonException)
        {
            return new ReverseSyncState();
        }
    }

    internal void Save(ReverseSyncState state)
    {
        Directory.CreateDirectory(applicationPaths.PluginConfigurationsPath);
        var temporaryPath = StatePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, Constants.JsonSerializerOptions));
        File.Move(temporaryPath, StatePath, true);
    }
}
