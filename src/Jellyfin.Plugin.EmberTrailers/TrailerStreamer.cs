using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EmberTrailers;

/// <summary>One stream YouTube offers: its address, how it is delivered and the headers YouTube expects.</summary>
public sealed record StreamInput(string Url, string Protocol, IReadOnlyDictionary<string, string> Headers, int Height, string Codec);

/// <summary>
/// One way to play a video: a single stream that already has picture and sound, or a picture stream
/// plus a sound stream that the server joins while sending.
/// </summary>
public sealed record StreamCandidate(string Label, StreamInput Video, StreamInput? Audio)
{
    public bool IsHls => Video.Protocol.StartsWith("m3u8", StringComparison.Ordinal);

    /// <summary>Gets a value indicating whether this is one ordinary file that can be passed through as is (and seeked).</summary>
    public bool IsProgressive => Audio is null && !IsHls;
}

/// <summary>Picks the streams to try from yt-dlp's description of a video.</summary>
public static class StreamPlanner
{
    public static IReadOnlyList<StreamCandidate> Plan(JsonElement info, int maxHeight)
    {
        if (!info.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<StreamCandidate>();
        }

        var all = new List<(StreamInput Input, bool HasVideo, bool HasAudio, double Tbr, double Abr, int LangPref, bool Original)>();
        foreach (var f in formats.EnumerateArray())
        {
            var url = YtDlpService.Str(f, "url");
            var protocol = YtDlpService.Str(f, "protocol");
            if (url.Length == 0 || !(protocol is "https" or "http" || protocol.StartsWith("m3u8", StringComparison.Ordinal)))
            {
                continue; // storyboards, DASH manifests, SABR and anything else ffmpeg can't read directly
            }

            if (f.TryGetProperty("has_drm", out var drm) && drm.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var vcodec = YtDlpService.Str(f, "vcodec");
            var acodec = YtDlpService.Str(f, "acodec");
            var hasVideo = vcodec.Length > 0 && vcodec != "none";
            var hasAudio = acodec.Length > 0 && acodec != "none";
            if (!hasVideo && !hasAudio)
            {
                continue;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (f.TryGetProperty("http_headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in h.EnumerateObject())
                {
                    if (kv.Value.ValueKind == JsonValueKind.String)
                    {
                        headers[kv.Name] = kv.Value.GetString() ?? string.Empty;
                    }
                }
            }

            var height = Num(f, "height");
            var note = YtDlpService.Str(f, "format_note");
            all.Add((
                new StreamInput(url, protocol, headers, (int)height, hasVideo ? vcodec : acodec),
                hasVideo,
                hasAudio,
                Num(f, "tbr"),
                Num(f, "abr"),
                (int)Num(f, "language_preference", -1),
                note.Contains("original", StringComparison.OrdinalIgnoreCase) || note.Contains("default", StringComparison.OrdinalIgnoreCase)));
        }

        static bool Avc(StreamInput i) => i.Codec.StartsWith("avc1", StringComparison.Ordinal) || i.Codec.StartsWith("h264", StringComparison.Ordinal);
        static bool Aac(StreamInput i) => i.Codec.StartsWith("mp4a", StringComparison.Ordinal) || i.Codec.StartsWith("aac", StringComparison.Ordinal);

        var fits = all.Where(x => !x.HasVideo || x.Input.Height <= maxHeight).ToList();
        var combined = fits.Where(x => x.HasVideo && x.HasAudio)
            .OrderByDescending(x => x.Input.Height).ThenByDescending(x => x.Tbr).ToList();
        var videoOnly = fits.Where(x => x.HasVideo && !x.HasAudio && !x.Input.Protocol.StartsWith("m3u8", StringComparison.Ordinal))
            .OrderByDescending(x => x.Input.Height).ThenByDescending(x => x.Tbr).ToList();
        var audioOnly = all.Where(x => !x.HasVideo && x.HasAudio && !x.Input.Protocol.StartsWith("m3u8", StringComparison.Ordinal))
            .OrderByDescending(x => x.Original).ThenByDescending(x => x.LangPref).ThenByDescending(x => x.Abr).ToList();

        var plans = new List<StreamCandidate>();
        void Add(StreamCandidate? c)
        {
            if (c is not null && plans.All(p => p.Video.Url != c.Video.Url || p.Audio?.Url != c.Audio?.Url))
            {
                plans.Add(c);
            }
        }

        // 1. YouTube's ready-combined HLS stream in H.264: starts fast, plays everywhere.
        var hlsAvc = combined.FirstOrDefault(x => x.Input.Protocol.StartsWith("m3u8", StringComparison.Ordinal) && Avc(x.Input));
        Add(hlsAvc.Input is null ? null : new StreamCandidate($"hls {hlsAvc.Input.Height}p", hlsAvc.Input, null));

        // 2. Separate H.264 picture + AAC sound, joined on the fly.
        var vAvc = videoOnly.FirstOrDefault(x => Avc(x.Input));
        var aAac = audioOnly.FirstOrDefault(x => Aac(x.Input));
        Add(vAvc.Input is null || aAac.Input is null ? null : new StreamCandidate($"joined {vAvc.Input.Height}p h264", vAvc.Input, aAac.Input));

        // 3. Any other combined HLS stream.
        var hlsAny = combined.FirstOrDefault(x => x.Input.Protocol.StartsWith("m3u8", StringComparison.Ordinal));
        Add(hlsAny.Input is null ? null : new StreamCandidate($"hls {hlsAny.Input.Height}p", hlsAny.Input, null));

        // 4. Best picture in any codec + best sound.
        var vAny = videoOnly.FirstOrDefault();
        var aAny = audioOnly.FirstOrDefault();
        Add(vAny.Input is null || aAny.Input is null ? null : new StreamCandidate($"joined {vAny.Input.Height}p {vAny.Input.Codec}", vAny.Input, aAny.Input));

        // 5. A plain combined file (usually 360p): lowest quality, but the simplest and seekable.
        var file = combined.FirstOrDefault(x => !x.Input.Protocol.StartsWith("m3u8", StringComparison.Ordinal));
        Add(file.Input is null ? null : new StreamCandidate($"file {file.Input.Height}p", file.Input, null));

        return plans;
    }

    /// <summary>When the stream addresses stop working (YouTube puts an "expire" time in them), minus a margin.</summary>
    public static DateTime ExpiresAt(IReadOnlyList<StreamCandidate> plans)
    {
        var limit = DateTime.UtcNow.AddHours(4);
        foreach (var p in plans)
        {
            foreach (var url in new[] { p.Video.Url, p.Audio?.Url })
            {
                var e = Expiry(url);
                if (e is not null && e.Value < limit)
                {
                    limit = e.Value;
                }
            }
        }

        return limit.AddMinutes(-15);
    }

    private static DateTime? Expiry(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return null;
        }

        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        var raw = q["expire"];
        if (raw is null)
        {
            // HLS addresses carry it in the path: /expire/1234567890/
            var segs = u.AbsolutePath.Split('/');
            var i = Array.IndexOf(segs, "expire");
            raw = i >= 0 && i + 1 < segs.Length ? segs[i + 1] : null;
        }

        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime
            : null;
    }

    private static double Num(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
}

/// <summary>Raised when no way of streaming a trailer worked. <see cref="Attempts"/> says what was tried.</summary>
public sealed class TrailerStreamException : Exception
{
    public TrailerStreamException(string message, IReadOnlyList<string> attempts)
        : base(message)
    {
        Attempts = attempts;
    }

    public IReadOnlyList<string> Attempts { get; }
}

/// <summary>A trailer stream that has already produced its first bytes, ready to be sent on.</summary>
public sealed class OpenedTrailer : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;

    public OpenedTrailer(string method, string contentType, Stream body, byte[] first, int firstLength, Func<ValueTask> dispose)
    {
        Method = method;
        ContentType = contentType;
        Body = body;
        First = first;
        FirstLength = firstLength;
        _dispose = dispose;
    }

    /// <summary>Gets how the trailer is being delivered (for diagnostics), e.g. "hls 1080p".</summary>
    public string Method { get; }

    public string ContentType { get; }

    public int StatusCode { get; init; } = 200;

    public long? ContentLength { get; init; }

    public string? ContentRange { get; init; }

    public bool AcceptRanges { get; init; }

    public Stream Body { get; }

    public byte[] First { get; }

    public int FirstLength { get; }

    public IReadOnlyList<string> Attempts { get; init; } = Array.Empty<string>();

    public ValueTask DisposeAsync() => _dispose();
}

/// <summary>
/// Opens a trailer stream, trying the fastest way first and falling back when YouTube refuses one:
/// 1. the direct YouTube streams yt-dlp found (passed through, or joined by Jellyfin's ffmpeg), as the
///    usual player and then as other YouTube player kinds;
/// 2. yt-dlp downloading and sending the video itself (slow to start, but handles anything).
/// Nothing is written to disk.
/// </summary>
public sealed class TrailerStreamer
{
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan YtDlpFirstByteTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(75);

    private readonly YtDlpService _yt;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TrailerStreamer> _log;

    public TrailerStreamer(YtDlpService yt, IHttpClientFactory http, ILogger<TrailerStreamer> log)
    {
        _yt = yt;
        _http = http;
        _log = log;
    }

    public async Task<OpenedTrailer> OpenAsync(string key, string? range, CancellationToken ct)
    {
        var attempts = new List<string>();
        var sw = Stopwatch.StartNew();
        foreach (var alt in new[] { false, true })
        {
            var (plans, reason) = await _yt.GetPlansAsync(key, alt, ct).ConfigureAwait(false);
            if (plans.Count == 0)
            {
                attempts.Add($"{(alt ? "other players" : "lookup")}: {reason}");
                continue;
            }

            foreach (var plan in plans)
            {
                if (sw.Elapsed > Budget - YtDlpFirstByteTimeout)
                {
                    break;
                }

                try
                {
                    var opened = plan.IsProgressive
                        ? await PassThroughAsync(plan, range, ct).ConfigureAwait(false)
                        : await RemuxAsync(plan, ct).ConfigureAwait(false);
                    _log.LogInformation("Ember Trailers: {Key} streaming as {Method} after {Ms} ms", key, opened.Method, sw.ElapsedMilliseconds);
                    return WithAttempts(opened, attempts);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    attempts.Add($"{(alt ? "other players, " : string.Empty)}{plan.Label}: {Short(e.Message)}");
                    _log.LogWarning("Ember Trailers: {Key} {Plan} failed: {Error}", key, plan.Label, e.Message);
                }
            }

            // These addresses didn't work; don't hand them out again.
            _yt.ForgetPlans(key);
        }

        try
        {
            var opened = await YtDlpPipeAsync(key, ct).ConfigureAwait(false);
            _log.LogInformation("Ember Trailers: {Key} streaming through yt-dlp after {Ms} ms", key, sw.ElapsedMilliseconds);
            return WithAttempts(opened, attempts);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            attempts.Add($"yt-dlp: {Short(e.Message)}");
        }

        throw new TrailerStreamException(attempts.LastOrDefault() ?? "The trailer couldn't be streamed.", attempts);
    }

    private static OpenedTrailer WithAttempts(OpenedTrailer t, List<string> attempts) =>
        new(t.Method, t.ContentType, t.Body, t.First, t.FirstLength, t.DisposeAsync)
        {
            StatusCode = t.StatusCode,
            ContentLength = t.ContentLength,
            ContentRange = t.ContentRange,
            AcceptRanges = t.AcceptRanges,
            Attempts = attempts.ToArray(),
        };

    private static string Short(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? s;
        return line.Length > 300 ? line[..300] : line;
    }

    /// <summary>A single ordinary file: passed straight through, including seeking (Range requests).</summary>
    private async Task<OpenedTrailer> PassThroughAsync(StreamCandidate plan, string? range, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var req = new HttpRequestMessage(HttpMethod.Get, plan.Video.Url);
        foreach (var (k, v) in plan.Video.Headers)
        {
            req.Headers.TryAddWithoutValidation(k, v);
        }

        if (!string.IsNullOrWhiteSpace(range))
        {
            req.Headers.TryAddWithoutValidation("Range", range);
        }

        using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headersCts.CancelAfter(FirstByteTimeout);
        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            var code = (int)resp.StatusCode;
            resp.Dispose();
            throw new IOException($"YouTube answered {code}");
        }

        var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var first = new byte[64 * 1024];
        int n;
        try
        {
            n = await body.ReadAsync(first, headersCts.Token).ConfigureAwait(false);
        }
        catch
        {
            resp.Dispose();
            throw;
        }

        if (n <= 0)
        {
            resp.Dispose();
            throw new IOException("YouTube sent an empty file");
        }

        return new OpenedTrailer(plan.Label, resp.Content.Headers.ContentType?.MediaType ?? "video/mp4", body, first, n, () =>
        {
            resp.Dispose();
            return ValueTask.CompletedTask;
        })
        {
            StatusCode = (int)resp.StatusCode,
            ContentLength = resp.Content.Headers.ContentLength,
            ContentRange = resp.Content.Headers.ContentRange?.ToString(),
            AcceptRanges = true,
        };
    }

    /// <summary>
    /// HLS, or separate picture and sound: Jellyfin's ffmpeg reads YouTube's streams directly and sends
    /// them on without re-encoding (MPEG-TS for HLS, Matroska for joined streams).
    /// </summary>
    private async Task<OpenedTrailer> RemuxAsync(StreamCandidate plan, CancellationToken ct)
    {
        var ffmpeg = _yt.FfmpegPath ?? throw new IOException("Jellyfin's ffmpeg wasn't found");
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        AddInput(args, plan.Video);
        if (plan.Audio is not null)
        {
            AddInput(args, plan.Audio);
            args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
        }
        else
        {
            args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });
        }

        args.AddRange(new[] { "-c", "copy" });
        var ts = plan.IsHls && plan.Video.Codec.StartsWith("avc1", StringComparison.Ordinal);
        args.AddRange(ts
            ? new[] { "-f", "mpegts", "pipe:1" }
            : new[] { "-f", "matroska", "-live", "1", "pipe:1" });
        return await StartPipeAsync(ffmpeg, args, plan.Label, ts ? "video/mp2t" : "video/x-matroska", FirstByteTimeout, ct).ConfigureAwait(false);
    }

    private static void AddInput(List<string> args, StreamInput input)
    {
        if (input.Headers.TryGetValue("User-Agent", out var ua))
        {
            args.AddRange(new[] { "-user_agent", ua });
        }

        var extra = new StringBuilder();
        foreach (var (k, v) in input.Headers)
        {
            if (!k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                extra.Append(k).Append(": ").Append(v).Append("\r\n");
            }
        }

        if (extra.Length > 0)
        {
            args.AddRange(new[] { "-headers", extra.ToString() });
        }

        args.AddRange(new[]
        {
            "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "5",
            "-rw_timeout", "15000000",
            "-i", input.Url,
        });
    }

    /// <summary>The last resort: yt-dlp fetches the video itself (and joins it with Jellyfin's ffmpeg).</summary>
    private async Task<OpenedTrailer> YtDlpPipeAsync(string key, CancellationToken ct)
    {
        var p = await _yt.StartStreamAsync(key, ct).ConfigureAwait(false);
        return await ReadFirstAsync(p, "yt-dlp", null, YtDlpFirstByteTimeout, ct).ConfigureAwait(false);
    }

    private static async Task<OpenedTrailer> StartPipeAsync(string exe, IEnumerable<string> args, string label, string contentType, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
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

        var p = Process.Start(psi) ?? throw new IOException($"Could not start {Path.GetFileName(exe)}");
        return await ReadFirstAsync(p, label, contentType, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a process's first bytes so a failure can still be answered with an error (and the next
    /// method tried) instead of an empty video.
    /// </summary>
    private static async Task<OpenedTrailer> ReadFirstAsync(Process p, string label, string? contentType, TimeSpan timeout, CancellationToken ct)
    {
        var err = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            var buf = new char[4096];
            int n;
            while ((n = await p.StandardError.ReadAsync(buf.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                lock (err)
                {
                    err.Append(buf, 0, n);
                    if (err.Length > 8000)
                    {
                        err.Remove(0, err.Length - 8000);
                    }
                }
            }
        });

        void Kill()
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        var stdout = p.StandardOutput.BaseStream;
        var first = new byte[64 * 1024];
        var n = 0;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try
            {
                // Collect a little before answering, so the content type can be told from the first bytes.
                while (n < 1024)
                {
                    var r = await stdout.ReadAsync(first.AsMemory(n), cts.Token).ConfigureAwait(false);
                    if (r <= 0)
                    {
                        break;
                    }

                    n += r;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill();
                p.Dispose();
                throw new TimeoutException($"No data after {timeout.TotalSeconds:0} seconds");
            }
            catch
            {
                Kill();
                p.Dispose();
                throw;
            }
        }

        if (n == 0)
        {
            Kill();
            await Task.WhenAny(errTask, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
            string msg;
            lock (err)
            {
                msg = err.ToString();
            }

            p.Dispose();
            throw new IOException(string.IsNullOrWhiteSpace(msg) ? "It stopped without sending anything" : msg);
        }

        // HLS arrives as MPEG-TS (starts with 0x47), joined streams as Matroska (0x1A45DFA3), plain files as MP4.
        contentType ??= first[0] == 0x47 ? "video/mp2t"
            : n >= 8 && first[4] == (byte)'f' && first[5] == (byte)'t' && first[6] == (byte)'y' && first[7] == (byte)'p' ? "video/mp4"
            : "video/x-matroska";
        return new OpenedTrailer(label, contentType, stdout, first, n, () =>
        {
            Kill();
            p.Dispose();
            return ValueTask.CompletedTask;
        });
    }
}
