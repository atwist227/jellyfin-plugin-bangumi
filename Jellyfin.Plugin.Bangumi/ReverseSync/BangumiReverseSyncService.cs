using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Bangumi.Model;
using Jellyfin.Plugin.Bangumi.OAuth;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jellyfin.Plugin.Bangumi.ReverseSync;

public sealed record ReverseSyncRunResult(
    int UsersSucceeded,
    int UsersFailed,
    int EpisodesMatched,
    int EpisodesChanged,
    bool AlreadyRunning = false);

public sealed class BangumiReverseSyncService(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    OAuthStore oauthStore,
    BangumiApi api,
    ReverseSyncStateStore stateStore,
    ReverseSyncWriteGuard writeGuard,
    Logger<BangumiReverseSyncService> log)
{
    private const int CollectionPageSize = 100;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ReverseSyncRunResult> RunAsync(
        bool preview,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
        {
            log.Warn("Bangumi 反向同步任务已在运行，本次跳过");
            return new ReverseSyncRunResult(0, 0, 0, 0, true);
        }

        try
        {
            if (!preview && Plugin.Instance?.Configuration.EnableReversePlaybackSync != true)
            {
                log.Info("Bangumi 反向同步未启用");
                return new ReverseSyncRunResult(0, 0, 0, 0);
            }

            oauthStore.Load();
            var oauthUsers = oauthStore.GetUsers().ToList();
            var state = stateStore.Load();
            var usersSucceeded = 0;
            var usersFailed = 0;
            var episodesMatched = 0;
            var episodesChanged = 0;

            for (var index = 0; index < oauthUsers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(oauthUsers.Count == 0 ? 100 : 100D * index / oauthUsers.Count);
                var (storedUserId, oauthUser) = oauthUsers[index];

                if (!Guid.TryParse(storedUserId, out var userId) || userManager.GetUserById(userId) is not { } jellyfinUser)
                {
                    usersFailed++;
                    log.Warn("OAuth 记录对应的 Jellyfin 用户不存在: {UserId}", storedUserId);
                    continue;
                }

                if (!oauthUser.Available)
                {
                    usersFailed++;
                    log.Warn("用户 #{UserId} 的 Bangumi OAuth 已过期或资料不完整", userId);
                    continue;
                }

                try
                {
                    var userState = state.Users.GetValueOrDefault(storedUserId) ?? new ReverseSyncUserState();
                    var result = await SyncUserAsync(
                        jellyfinUser,
                        oauthUser,
                        userState,
                        preview,
                        cancellationToken);
                    episodesMatched += result.Matched;
                    episodesChanged += result.Changed;
                    usersSucceeded++;

                    if (!preview)
                        state.Users[storedUserId] = result.State;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    usersFailed++;
                    log.Error("用户 #{UserId} 的 Bangumi 反向同步失败: {Error}", userId, exception);
                }
            }

            if (!preview && usersSucceeded > 0)
                stateStore.Save(state);

            progress?.Report(100);
            return new ReverseSyncRunResult(usersSucceeded, usersFailed, episodesMatched, episodesChanged);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<UserSyncResult> SyncUserAsync(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        OAuthUser oauthUser,
        ReverseSyncUserState previousState,
        bool preview,
        CancellationToken cancellationToken)
    {
        var localEpisodes = GetLocalUnplayedEpisodes(jellyfinUser);
        var localBySubject = localEpisodes
            .GroupBy(episode => episode.SubjectId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var localSnapshots = localBySubject.ToDictionary(
            pair => pair.Key,
            pair => string.Join(',', pair.Value.Select(item => item.EpisodeId).OrderBy(id => id)));

        var collections = await GetAllCollectionsWithRetry(oauthUser, cancellationToken);
        var collectionBySubject = collections
            .GroupBy(collection => collection.Id)
            .ToDictionary(group => group.Key, group => group.Last());
        var subjectSnapshots = collectionBySubject.ToDictionary(
            pair => pair.Key,
            pair => $"{pair.Key}:{(int)pair.Value.Status}:{pair.Value.EpisodeStatus}");

        var now = DateTime.UtcNow;
        var fullIntervalDays = Math.Max(1, Plugin.Instance?.Configuration.ReverseSyncFullIntervalDays ?? 7);
        var fullSync = previousState.LastFullSyncUtc is null
            || now - previousState.LastFullSyncUtc.Value >= TimeSpan.FromDays(fullIntervalDays);
        var changedSubjects = ReverseSyncPlanner.SelectChangedSubjects(
            localBySubject.Keys,
            previousState.SubjectSnapshots,
            subjectSnapshots,
            previousState.LocalEpisodeSnapshots,
            localSnapshots,
            fullSync);

        log.Info(
            "用户 #{UserId} 开始 Bangumi {Mode}反向同步：{Subjects} 个条目需要核对",
            jellyfinUser.Id,
            fullSync ? "完整" : "增量",
            changedSubjects.Count);

        var matched = 0;
        var changed = 0;
        foreach (var subjectId in changedSubjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!collectionBySubject.ContainsKey(subjectId))
            {
                log.Info("Subject #{SubjectId} 不在用户 Bangumi 收藏摘要中，跳过", subjectId);
                continue;
            }

            var remotePage = await ExecuteWithRetry(
                () => api.GetEpisodeCollectionInfo(oauthUser.AccessToken, subjectId, cancellationToken),
                cancellationToken);
            if (remotePage is null)
                throw new InvalidOperationException($"Bangumi 未返回 Subject #{subjectId} 的分集收藏数据");

            var plan = ReverseSyncPlanner.Create(subjectId, localBySubject[subjectId], remotePage.Data);
            foreach (var issue in plan.Issues)
                log.Warn("Subject #{SubjectId}, Episode #{EpisodeId}: {Message}", issue.SubjectId, issue.EpisodeId, issue.Message);

            matched += plan.EpisodesToMarkPlayed.Count;
            foreach (var localEpisode in plan.EpisodesToMarkPlayed)
            {
                log.Info(
                    "{Action} Jellyfin 用户 #{UserId} 的 {Name} (Bangumi Episode #{EpisodeId}) 为已播放",
                    preview ? "将标记" : "标记",
                    jellyfinUser.Id,
                    localEpisode.Name,
                    localEpisode.EpisodeId);
                if (preview)
                    continue;

                var item = libraryManager.GetItemById(localEpisode.ItemId);
                if (item is null)
                {
                    log.Warn("待修改的 Jellyfin 项目 #{ItemId} 已不存在，跳过", localEpisode.ItemId);
                    continue;
                }

                var userData = userDataManager.GetUserData(jellyfinUser, item);
                if (userData is null)
                {
                    log.Warn("项目 #{ItemId} 没有可写入的用户数据，跳过", item.Id);
                    continue;
                }

                if (userData.Played)
                    continue;

                userData.Played = true;
                userData.PlayCount = Math.Max(1, userData.PlayCount);
                userData.LastPlayedDate = DateTime.UtcNow;
                userData.PlaybackPositionTicks = 0;
                using (writeGuard.Suppress(jellyfinUser.Id, item.Id))
                {
                    userDataManager.SaveUserData(
                        jellyfinUser,
                        item,
                        userData,
                        UserDataSaveReason.UpdateUserData,
                        cancellationToken);
                }

                changed++;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        var newState = new ReverseSyncUserState
        {
            LastSuccessfulSyncUtc = now,
            LastFullSyncUtc = fullSync ? now : previousState.LastFullSyncUtc,
            SubjectSnapshots = subjectSnapshots,
            LocalEpisodeSnapshots = localSnapshots
        };
        return new UserSyncResult(matched, changed, newState);
    }

    private List<LocalEpisode> GetLocalUnplayedEpisodes(Jellyfin.Database.Implementations.Entities.User user)
    {
        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            User = user,
            IsPlayed = false
        });
        var result = new List<LocalEpisode>();
        foreach (var item in items.OfType<JellyfinEpisode>())
        {
            if (!int.TryParse(item.GetProviderId(Constants.ProviderName), out var episodeId))
                continue;

            var subjectId = FindSubjectId(item);
            if (subjectId is null)
            {
                log.Warn("{Name} (#{ItemId}) 有 Bangumi Episode ID，但父级没有 Subject ID，跳过", item.Name, item.Id);
                continue;
            }

            result.Add(new LocalEpisode(item.Id, subjectId.Value, episodeId, item.Name));
        }

        return result;
    }

    private static int? FindSubjectId(BaseItem item)
    {
        for (var parent = item.GetParent(); parent is not null; parent = parent.GetParent())
        {
            if (int.TryParse(parent.GetProviderId(Constants.ProviderName), out var subjectId))
                return subjectId;
        }

        return null;
    }

    private async Task<List<SubjectCollectionInfo>> GetAllCollectionsWithRetry(
        OAuthUser oauthUser,
        CancellationToken cancellationToken)
    {
        var result = new List<SubjectCollectionInfo>();
        for (var offset = 0;; offset += CollectionPageSize)
        {
            var page = await ExecuteWithRetry(
                () => api.GetUserCollections(
                    oauthUser.AccessToken,
                    oauthUser.UserName,
                    CollectionPageSize,
                    offset,
                    cancellationToken),
                cancellationToken);
            if (page is null)
                throw new InvalidOperationException("Bangumi 未返回收藏摘要");

            var data = page.Data.ToList();
            result.AddRange(data);
            if (result.Count >= page.Total || data.Count == 0)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return result;
    }

    private async Task<T?> ExecuteWithRetry<T>(Func<Task<T?>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException exception) when (attempt < 3 && IsTransient(exception.StatusCode))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                log.Warn("Bangumi 请求暂时失败，{Delay} 秒后重试: {Error}", delay.TotalSeconds, exception.Message);
                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException) when (attempt < 3 && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                log.Warn("Bangumi 请求超时，{Delay} 秒后重试", delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode? statusCode)
    {
        return statusCode is null
            || statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;
    }

    private sealed record UserSyncResult(int Matched, int Changed, ReverseSyncUserState State);
}
