using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.ReverseSync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Bangumi.ScheduledTask;

public sealed class BangumiReverseSyncPreviewTask(BangumiReverseSyncService service) : IScheduledTask
{
    public string Key => "BangumiReverseSyncPreviewTask";

    public string Name => "预览 Bangumi 反向同步";

    public string Description => "只在日志中列出精确匹配和待修改项目，不写入 Jellyfin 播放状态";

    public string Category => Constants.PluginName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await service.RunAsync(true, progress, cancellationToken);
    }
}
