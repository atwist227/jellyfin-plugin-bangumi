using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.ReverseSync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Bangumi.ScheduledTask;

public sealed class BangumiReverseSyncTask(BangumiReverseSyncService service) : IScheduledTask
{
    public string Key => "BangumiReverseSyncTask";

    public string Name => "从 Bangumi 同步已看状态";

    public string Description => "按精确 Episode ID 将 Bangumi 已看状态单向增加到 Jellyfin；不会取消已看或同步续播位置";

    public string Category => Constants.PluginName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(30).Ticks,
                MaxRuntimeTicks = TimeSpan.FromMinutes(20).Ticks
            }
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await service.RunAsync(false, progress, cancellationToken);
    }
}
