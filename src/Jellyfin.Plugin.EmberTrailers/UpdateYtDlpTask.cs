using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.EmberTrailers;

/// <summary>Keeps yt-dlp current, so YouTube changes fix themselves overnight.</summary>
public class UpdateYtDlpTask : IScheduledTask
{
    private readonly YtDlpService _yt;

    public UpdateYtDlpTask(YtDlpService yt)
    {
        _yt = yt;
    }

    public string Name => "Update trailer downloader (yt-dlp)";

    public string Key => "EmberTrailersUpdateYtDlp";

    public string Description => "Downloads the latest yt-dlp so trailers keep playing when YouTube changes.";

    public string Category => "Ember Trailers";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(5);
        await _yt.UpdateAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
#if JF1011
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
#else
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
#endif
    }
}
