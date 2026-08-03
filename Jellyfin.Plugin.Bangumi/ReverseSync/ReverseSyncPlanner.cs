using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Bangumi.Model;

namespace Jellyfin.Plugin.Bangumi.ReverseSync;

internal sealed record LocalEpisode(Guid ItemId, int SubjectId, int EpisodeId, string Name);

internal sealed record ReverseSyncIssue(int SubjectId, int? EpisodeId, string Message);

internal sealed record ReverseSyncPlan(
    IReadOnlyList<LocalEpisode> EpisodesToMarkPlayed,
    IReadOnlyList<ReverseSyncIssue> Issues);

internal static class ReverseSyncPlanner
{
    public static IReadOnlyList<int> SelectChangedSubjects(
        IEnumerable<int> localSubjectIds,
        IReadOnlyDictionary<int, string> previousSubjectSnapshots,
        IReadOnlyDictionary<int, string> currentSubjectSnapshots,
        IReadOnlyDictionary<int, string> previousLocalSnapshots,
        IReadOnlyDictionary<int, string> currentLocalSnapshots,
        bool fullSync)
    {
        return localSubjectIds
            .Where(subjectId => fullSync
                || !SnapshotEquals(previousSubjectSnapshots, currentSubjectSnapshots, subjectId)
                || !SnapshotEquals(previousLocalSnapshots, currentLocalSnapshots, subjectId))
            .Distinct()
            .OrderBy(subjectId => subjectId)
            .ToList();
    }

    public static ReverseSyncPlan Create(
        int subjectId,
        IEnumerable<LocalEpisode> localEpisodes,
        IEnumerable<EpisodeCollectionInfo> remoteEpisodes)
    {
        var changes = new List<LocalEpisode>();
        var issues = new List<ReverseSyncIssue>();
        var localGroups = localEpisodes.GroupBy(episode => episode.EpisodeId).ToDictionary(group => group.Key);
        var remoteGroups = remoteEpisodes
            .Where(info => info.Episode is { Id: > 0 })
            .GroupBy(info => info.Episode!.Id)
            .ToDictionary(group => group.Key);

        foreach (var (episodeId, localGroup) in localGroups)
        {
            var localMatches = localGroup.ToList();
            if (localMatches.Count != 1)
            {
                issues.Add(new ReverseSyncIssue(subjectId, episodeId, "本地存在重复 Episode ID，已跳过"));
                continue;
            }

            if (!remoteGroups.TryGetValue(episodeId, out var remoteGroup))
            {
                issues.Add(new ReverseSyncIssue(subjectId, episodeId, "Bangumi 返回中缺少精确 Episode ID，已跳过"));
                continue;
            }

            var remoteMatches = remoteGroup.ToList();
            if (remoteMatches.Count != 1)
            {
                issues.Add(new ReverseSyncIssue(subjectId, episodeId, "Bangumi 返回了重复 Episode ID，已跳过"));
                continue;
            }

            var remote = remoteMatches[0];
            if (remote.Episode!.ParentId != subjectId)
            {
                issues.Add(new ReverseSyncIssue(subjectId, episodeId, "Episode ID 所属 Subject 冲突，已跳过"));
                continue;
            }

            if (remote.Type == EpisodeCollectionType.Watched)
                changes.Add(localMatches[0]);
        }

        return new ReverseSyncPlan(changes, issues);
    }

    private static bool SnapshotEquals(
        IReadOnlyDictionary<int, string> previous,
        IReadOnlyDictionary<int, string> current,
        int subjectId)
    {
        return previous.TryGetValue(subjectId, out var previousValue)
            && current.TryGetValue(subjectId, out var currentValue)
            && string.Equals(previousValue, currentValue, StringComparison.Ordinal);
    }
}
