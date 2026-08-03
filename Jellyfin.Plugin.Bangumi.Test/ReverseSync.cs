using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Bangumi.Model;
using Jellyfin.Plugin.Bangumi.ReverseSync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.Bangumi.Test;

[TestClass]
public class ReverseSync
{
    [TestMethod]
    public void ExactWatchedEpisodeIsPlanned()
    {
        var local = new LocalEpisode(Guid.NewGuid(), 100, 200, "Episode 1");
        var plan = ReverseSyncPlanner.Create(100, [local], [Remote(100, 200, EpisodeCollectionType.Watched)]);

        Assert.AreEqual(1, plan.EpisodesToMarkPlayed.Count);
        Assert.AreEqual(local.ItemId, plan.EpisodesToMarkPlayed[0].ItemId);
        Assert.AreEqual(0, plan.Issues.Count);
    }

    [TestMethod]
    public void UnwatchedEpisodeNeverRequestsAnUnwatch()
    {
        var local = new LocalEpisode(Guid.NewGuid(), 100, 200, "Episode 1");
        var plan = ReverseSyncPlanner.Create(100, [local], [Remote(100, 200, EpisodeCollectionType.Default)]);

        Assert.AreEqual(0, plan.EpisodesToMarkPlayed.Count);
    }

    [TestMethod]
    public void MissingEpisodeIdIsSkipped()
    {
        var local = new LocalEpisode(Guid.NewGuid(), 100, 200, "Episode 1");
        var plan = ReverseSyncPlanner.Create(100, [local], [Remote(100, 201, EpisodeCollectionType.Watched)]);

        Assert.AreEqual(0, plan.EpisodesToMarkPlayed.Count);
        StringAssert.Contains(plan.Issues.Single().Message, "缺少精确 Episode ID");
    }

    [TestMethod]
    public void DuplicateLocalEpisodeIdIsSkipped()
    {
        var plan = ReverseSyncPlanner.Create(
            100,
            [
                new LocalEpisode(Guid.NewGuid(), 100, 200, "Episode 1 copy A"),
                new LocalEpisode(Guid.NewGuid(), 100, 200, "Episode 1 copy B")
            ],
            [Remote(100, 200, EpisodeCollectionType.Watched)]);

        Assert.AreEqual(0, plan.EpisodesToMarkPlayed.Count);
        StringAssert.Contains(plan.Issues.Single().Message, "本地存在重复");
    }

    [TestMethod]
    public void DuplicateRemoteEpisodeIdIsSkipped()
    {
        var local = new LocalEpisode(Guid.NewGuid(), 100, 200, "Episode 1");
        var plan = ReverseSyncPlanner.Create(
            100,
            [local],
            [Remote(100, 200, EpisodeCollectionType.Watched), Remote(100, 200, EpisodeCollectionType.Watched)]);

        Assert.AreEqual(0, plan.EpisodesToMarkPlayed.Count);
        StringAssert.Contains(plan.Issues.Single().Message, "Bangumi 返回了重复");
    }

    [TestMethod]
    public void ConflictingSubjectIdIsSkipped()
    {
        var local = new LocalEpisode(Guid.NewGuid(), 100, 200, "Episode 1");
        var plan = ReverseSyncPlanner.Create(100, [local], [Remote(101, 200, EpisodeCollectionType.Watched)]);

        Assert.AreEqual(0, plan.EpisodesToMarkPlayed.Count);
        StringAssert.Contains(plan.Issues.Single().Message, "所属 Subject 冲突");
    }

    [TestMethod]
    public void WriteGuardIsScopedByUserAndItem()
    {
        var guard = new ReverseSyncWriteGuard();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        using (guard.Suppress(user, item))
        {
            Assert.IsTrue(guard.IsSuppressed(user, item));
            Assert.IsFalse(guard.IsSuppressed(Guid.NewGuid(), item));
            Assert.IsFalse(guard.IsSuppressed(user, Guid.NewGuid()));
        }

        Assert.IsFalse(guard.IsSuppressed(user, item));
    }

    [TestMethod]
    public void IncrementalSnapshotOnlySelectsChangedSubjects()
    {
        var previousSubjects = new Dictionary<int, string> { [100] = "100:3:1", [101] = "101:3:2" };
        var currentSubjects = new Dictionary<int, string> { [100] = "100:3:1", [101] = "101:3:3" };
        var previousLocal = new Dictionary<int, string> { [100] = "1,2", [101] = "3,4" };
        var currentLocal = new Dictionary<int, string> { [100] = "1,2", [101] = "3,4" };

        var changed = ReverseSyncPlanner.SelectChangedSubjects(
            [100, 101], previousSubjects, currentSubjects, previousLocal, currentLocal, false);

        CollectionAssert.AreEqual(new[] { 101 }, changed.ToArray());
    }

    [TestMethod]
    public void NewLocalEpisodeTriggersIncrementalCheck()
    {
        var subjects = new Dictionary<int, string> { [100] = "100:3:1" };
        var changed = ReverseSyncPlanner.SelectChangedSubjects(
            [100],
            subjects,
            subjects,
            new Dictionary<int, string> { [100] = "1" },
            new Dictionary<int, string> { [100] = "1,2" },
            false);

        CollectionAssert.AreEqual(new[] { 100 }, changed.ToArray());
    }

    [TestMethod]
    public void FullCheckSelectsAllLocalSubjects()
    {
        var unchanged = new Dictionary<int, string> { [100] = "same", [101] = "same" };
        var changed = ReverseSyncPlanner.SelectChangedSubjects(
            [101, 100], unchanged, unchanged, unchanged, unchanged, true);

        CollectionAssert.AreEqual(new[] { 100, 101 }, changed.ToArray());
    }

    private static EpisodeCollectionInfo Remote(
        int subjectId,
        int episodeId,
        EpisodeCollectionType type)
    {
        return new EpisodeCollectionInfo
        {
            Episode = new Jellyfin.Plugin.Bangumi.Model.Episode { Id = episodeId, ParentId = subjectId },
            Type = type
        };
    }
}
