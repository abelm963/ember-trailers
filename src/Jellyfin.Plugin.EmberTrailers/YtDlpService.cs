using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EmberTrailers;

public sealed record VideoMeta(string Key, string Title, string Channel, bool Verified, double Duration, bool Playable);

/// <summary>Keeps yt-dlp present and up to date, checks videos, and streams them.</summary>
public sealed partial class YtDlpService
{
    private static readonly TimeSpan MetaTtl = TimeSpan.FromHours(24);
    private readonly IHttpClientFactory _http;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<YtDlpService> _log;
    private readonly ConcurrentDictionary<string, (DateTime At, VideoMeta Meta)> _meta = new();
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public YtDlpService(IHttpClientFactory http, IMediaEncoder encoder, ILogger<YtDlpService> log)
    {
        _http = http;
        _encoder = encoder;
        _log = log;
    }

    public DateTime? LastSuccess { get; private set; }

    public DateTime? LastFailure { get; private set; }

    public int FailuresInARow { get; private set; }

    public string? LastError { get; private set; }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex KeyRegex();

    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    /// <summary>Extracts the 11-character video id from any YouTube link.</summary>
    public static string? KeyFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return null;
        }

        string? key = null;
        if (u.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            key = u.AbsolutePath.Trim('/');
        }
        else if (u.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            key = q["v"];
            if (key is null && u.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal))
            {
                key = u.AbsolutePath["/embed/".Length..];
            }
        }

        return IsValidKey(key) ? key : null;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string AssetName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "yt-dlp.exe";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "yt-dlp_linux_aarch64",
            Architecture.Arm => "yt-dlp_linux_armv7l",
            _ => "yt-dlp_linux",
        };
    }

    public string BinaryPath
    {
        get
        {
            var custom = Config.YtDlpPath;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            {
                return custom;
            }

            var folder = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "ember-trailers");
            return Path.Combine(folder, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
        }
    }

    /// <summary>Downloads the latest yt-dlp release. YouTube changes often break older versions.</summary>
    public async Task UpdateAsync(CancellationToken ct)
    {
        await _updateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = BinaryPath;
            if (!string.IsNullOrWhiteSpace(Config.YtDlpPath) && target == Config.YtDlpPath)
            {
                // A custom binary is managed by the admin; ask it to update itself.
                await RunAsync(new[] { "-U" }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var url = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{AssetName()}";
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            var tmp = target + ".download";
            await using (var src = await client.GetStreamAsync(url, ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            File.Move(tmp, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            _log.LogInformation("Ember Trailers: yt-dlp updated ({Version})", await VersionAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task EnsureAsync(CancellationToken ct)
    {
        if (!File.Exists(BinaryPath))
        {
            await UpdateAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        if (!File.Exists(BinaryPath))
        {
            return null;
        }

        var (code, output, _) = await RunAsync(new[] { "--version" }, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return code == 0 ? output.Trim() : null;
    }

    /// <summary>Looks up a video's title, channel and whether the channel is verified (cached for a day).</summary>
    public async Task<VideoMeta?> GetMetaAsync(string key, CancellationToken ct)
    {
        if (_meta.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < MetaTtl)
        {
            return hit.Meta;
        }

        await EnsureAsync(ct).ConfigureAwait(false);
        var (code, output, error) = await RunAsync(
            new[] { "-J", "--no-playlist", "--no-warnings", "--skip-download", $"https://www.youtube.com/watch?v={key}" },
            TimeSpan.FromSeconds(45),
            ct).ConfigureAwait(false);
        if (code != 0)
        {
            Fail(error);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var r = doc.RootElement;
            var meta = new VideoMeta(
                key,
                Str(r, "title"),
                Str(r, "channel") is { Length: > 0 } c ? c : Str(r, "uploader"),
                r.TryGetProperty("channel_is_verified", out var v) && v.ValueKind == JsonValueKind.True,
                r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
                Str(r, "availability") is "" or "public" or "unlisted");
            _meta[key] = (DateTime.UtcNow, meta);
            Succeed();
            return meta;
        }
        catch (JsonException e)
        {
            Fail(e.Message);
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    /// <summary>
    /// Starts yt-dlp writing the trailer (best H.264 video up to the height limit plus AAC audio,
    /// merged by Jellyfin's own ffmpeg into Matroska) to its standard output.
    /// </summary>
    public async Task<Process> StartStreamAsync(string key, CancellationToken ct)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        var h = Math.Clamp(Config.MaxHeight, 360, 2160);
        var format = $"bv*[height<={h}][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<={h}]+ba/b[height<={h}]/b";
        var args = new List<string>
        {
            "--no-playlist", "--no-warnings", "--quiet", "--no-part", "--no-cache-dir",
            "-f", format,
            "--merge-output-format", "mkv",
            "-o", "-",
        };
        var ffmpeg = _encoder.EncoderPath;
        if (!string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg);
        }

        args.Add($"https://www.youtube.com/watch?v={key}");
        var psi = new ProcessStartInfo(BinaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp.");
        return p;
    }

    public void Succeed()
    {
        LastSuccess = DateTime.UtcNow;
        FailuresInARow = 0;
        LastError = null;
    }

    public void Fail(string? error)
    {
        LastFailure = DateTime.UtcNow;
        FailuresInARow++;
        LastError = string.IsNullOrWhiteSpace(error) ? "unknown error" : error.Trim().Split('\n').Last();
        _log.LogWarning("Ember Trailers: yt-dlp failed ({Count} in a row): {Error}", FailuresInARow, LastError);
        if (FailuresInARow == 3)
        {
            // YouTube probably changed something; a newer yt-dlp usually fixes it.
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log.LogWarning(e, "Ember Trailers: automatic yt-dlp update failed");
                }
            });
        }
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(BinaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return (-1, string.Empty, "timed out");
        }

        return (p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }
}
