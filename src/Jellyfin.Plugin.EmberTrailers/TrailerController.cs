using System.Diagnostics;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EmberTrailers;

/// <summary>Trailer endpoints used by Ember. All require a signed-in Jellyfin user.</summary>
[ApiController]
[Route("EmberTrailers")]
[Authorize]
public class TrailerController : ControllerBase
{
    private readonly YtDlpService _yt;
    private readonly ILibraryManager _library;
    private readonly CalendarService _calendar;
    private readonly TrailerStreamer _streamer;
    private readonly IHttpClientFactory _http;

    public TrailerController(YtDlpService yt, ILibraryManager library, CalendarService calendar, TrailerStreamer streamer, IHttpClientFactory http)
    {
        _yt = yt;
        _library = library;
        _calendar = calendar;
        _streamer = streamer;
        _http = http;
    }

    /// <summary>
    /// Saves the calendar keys (admins only). Keys stay in the plugin's settings on this server and are
    /// never sent back to apps; only whether one is set is reported.
    /// </summary>
    [HttpPost("CalendarKeys")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult SetCalendarKeys([FromBody] CalendarKeys keys)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        if (keys.TraktClientId is not null)
        {
            plugin.Configuration.TraktClientId = keys.TraktClientId.Trim();
        }

        if (keys.TmdbApiKey is not null)
        {
            plugin.Configuration.TmdbApiKey = keys.TmdbApiKey.Trim();
        }

        plugin.SaveConfiguration();
        _calendar.Clear();
        return NoContent();
    }

    /// <summary>
    /// Ember's release calendar: popular movies (cinema and digital dates), new series and new seasons
    /// for the next <paramref name="days"/> days, from TMDB with the key Jellyfin already uses. Cached for 6 hours.
    /// </summary>
    [HttpGet("Calendar")]
    public async Task<ActionResult<object>> Calendar([FromQuery] string? region, [FromQuery] int days = 120, CancellationToken ct = default)
    {
        try
        {
            var items = await _calendar.GetAsync(region ?? "US", days, ct).ConfigureAwait(false);
            return new { region = (region ?? "US").ToUpperInvariant(), keySource = _calendar.KeySource, trakt = _calendar.TraktUsed, items };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = e.Message });
        }
    }

    /// <summary>Whether trailer streaming works right now.</summary>
    [HttpGet("Health")]
    public async Task<ActionResult<object>> Health(CancellationToken ct)
    {
        var version = await _yt.VersionAsync(ct).ConfigureAwait(false);
        if (version is not null && !_yt.HasDeno)
        {
            _ = _yt.EnsureDenoAsync(CancellationToken.None);
        }
        string status = version is null ? "Offline" : _yt.FailuresInARow >= 3 ? "Degraded" : "Online";
        return new
        {
            status,
            ytDlpVersion = version,
            lastSuccess = _yt.LastSuccess,
            lastFailure = _yt.LastFailure,
            failuresInARow = _yt.FailuresInARow,
            lastError = _yt.LastError,
            maxHeight = Plugin.Instance?.Configuration.MaxHeight,
            verifiedOnly = Plugin.Instance?.Configuration.VerifiedOnly,
            features = new[] { "trailers", "calendar", "reasons", "streamtest", "hls", "directstreams", "streamerrors" },
            ffmpeg = _yt.FfmpegPath is not null,
            jsRuntime = _yt.HasDeno ? "deno" : null,
            jsRuntimeError = _yt.DenoError,
            calendarKey = _calendar.ApiKey() is null ? "none" : _calendar.KeySource,
            calendarError = _calendar.LastError,
            traktConfigured = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TraktClientId),
        };
    }

    /// <summary>The trailer to play for a library item: an admin override, else the first official one that passes the checks.</summary>
    [HttpGet("Items/{itemId}/Info")]
    public async Task<ActionResult<object>> ItemInfo([FromRoute] Guid itemId, CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var candidates = new List<(string Key, string Source)>();
        var ov = cfg.Overrides.FirstOrDefault(o => string.Equals(o.ItemId, itemId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(o.ItemId, itemId.ToString(), StringComparison.OrdinalIgnoreCase));
        if (ov is not null && YtDlpService.IsValidKey(ov.YouTubeKey))
        {
            candidates.Add((ov.YouTubeKey, "override"));
        }

        // Trailers TMDB marks as official are trusted even when YouTube doesn't show a verified badge.
        var tmdbId = item.GetProviderId("Tmdb");
        var mediaType = item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : "movie";
        if (int.TryParse(tmdbId, out var tid))
        {
            foreach (var key in await _calendar.OfficialTrailerKeysAsync(mediaType, tid, ct).ConfigureAwait(false))
            {
                if (candidates.All(c => c.Key != key))
                {
                    candidates.Add((key, "tmdb-official"));
                }
            }
        }

        foreach (var t in item.RemoteTrailers ?? Array.Empty<MediaBrowser.Model.Entities.MediaUrl>())
        {
            var key = YtDlpService.KeyFromUrl(t.Url);
            if (key is not null && candidates.All(c => c.Key != key))
            {
                candidates.Add((key, "tmdb"));
            }
        }

        var reasons = new List<string>();
        foreach (var (key, source) in candidates.Take(5))
        {
            var (r, reason) = await CheckWithReason(key, source is "override" or "tmdb-official", ct).ConfigureAwait(false);
            if (r is not null)
            {
                _yt.WarmPlans(key);
                return new { available = true, key, source, title = r.Title, channel = r.Channel, verified = r.Verified, duration = r.Duration };
            }

            reasons.Add(reason ?? "not playable");
        }

        return new
        {
            available = false,
            key = (string?)null,
            source = (string?)null,
            reason = candidates.Count == 0 ? "No trailer is listed for this title." : reasons.FirstOrDefault(),
            reasons,
        };
    }

    /// <summary>Checks a specific YouTube video (used for titles that are not in the library yet, from Seerr).</summary>
    [HttpGet("Videos/{key}/Info")]
    public async Task<ActionResult<object>> VideoInfo([FromRoute] string key, CancellationToken ct)
    {
        if (!YtDlpService.IsValidKey(key))
        {
            return BadRequest();
        }

        var (r, reason) = await CheckWithReason(key, false, ct).ConfigureAwait(false);
        if (r is not null)
        {
            _yt.WarmPlans(key);
        }

        return r is null
            ? new { available = false, key, reason }
            : new { available = true, key, title = r.Title, channel = r.Channel, verified = r.Verified, duration = r.Duration };
    }

    private async Task<(VideoMeta? Meta, string? Reason)> CheckWithReason(string key, bool trusted, CancellationToken ct)
    {
        var (meta, reason) = await _yt.GetMetaWithReasonAsync(key, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return (null, reason);
        }

        if (!meta.Playable)
        {
            return (null, "The video is private or blocked.");
        }

        var verifiedOnly = Plugin.Instance?.Configuration.VerifiedOnly ?? true;
        if (verifiedOnly && !trusted && !meta.Verified)
        {
            return (null, $"From an unverified channel ({meta.Channel}).");
        }

        return (meta, null);
    }

    /// <summary>
    /// Admin diagnostics: runs the real streaming pipeline for up to 40 seconds and reports how it went,
    /// including every method that was tried.
    /// </summary>
    [HttpGet("Videos/{key}/Test")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<object>> TestStream([FromRoute] string key, CancellationToken ct)
    {
        if (!YtDlpService.IsValidKey(key))
        {
            return BadRequest();
        }

        var sw = Stopwatch.StartNew();
        var (meta, reason) = await _yt.GetMetaWithReasonAsync(key, ct).ConfigureAwait(false);
        var infoMs = sw.ElapsedMilliseconds;
        if (meta is null)
        {
            return new { ok = false, stage = "info", infoMs, error = reason };
        }

        sw.Restart();
        OpenedTrailer t;
        try
        {
            t = await _streamer.OpenAsync(key, null, ct).ConfigureAwait(false);
        }
        catch (TrailerStreamException e)
        {
            return new { ok = false, stage = "stream", infoMs, error = e.Message, attempts = e.Attempts, deno = _yt.HasDeno, ffmpeg = _yt.FfmpegPath is not null };
        }

        var firstByteMs = sw.ElapsedMilliseconds;
        long total = t.FirstLength;
        await using (t)
        {
            var buffer = new byte[64 * 1024];
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(40));
            try
            {
                int n;
                while (total < 8_000_000 && (n = await t.Body.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
                {
                    total += n;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }

        var seconds = sw.Elapsed.TotalSeconds;
        return new
        {
            ok = total > 500_000,
            stage = "stream",
            method = t.Method,
            infoMs,
            firstByteMs,
            bytes = total,
            mbps = seconds > 0 ? Math.Round(total * 8 / seconds / 1_000_000, 1) : 0,
            container = t.ContentType,
            hls = meta.Hls is not null,
            deno = _yt.HasDeno,
            ffmpeg = _yt.FfmpegPath is not null,
            channel = meta.Channel,
            attempts = t.Attempts,
            error = total > 500_000 ? null : t.Attempts.LastOrDefault() ?? "The stream stopped early.",
        };
    }

    /// <summary>
    /// Streams the trailer while it is fetched. Nothing is written to disk. When it can't be streamed the
    /// answer is 502 with the reason in the X-Ember-Error header and a JSON body, so apps can say why.
    /// </summary>
    [HttpGet("Videos/{key}/Stream")]
    public async Task Stream([FromRoute] string key, CancellationToken ct)
    {
        if (!YtDlpService.IsValidKey(key))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        OpenedTrailer t;
        try
        {
            t = await _streamer.OpenAsync(key, Request.Headers.Range.ToString(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // the viewer gave up
        }
        catch (Exception e)
        {
            _yt.Fail(e.Message);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            Response.Headers["X-Ember-Error"] = HeaderSafe(e.Message);
            await Response.WriteAsJsonAsync(
                new { error = e.Message, attempts = (e as TrailerStreamException)?.Attempts ?? Array.Empty<string>() },
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await using (t)
        {
            _yt.Succeed();
            Response.StatusCode = t.StatusCode;
            Response.ContentType = t.ContentType;
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Ember-Method"] = HeaderSafe(t.Method);
            if (t.ContentLength is { } len)
            {
                Response.ContentLength = len;
            }

            if (t.ContentRange is { Length: > 0 } cr)
            {
                Response.Headers.ContentRange = cr;
            }

            Response.Headers.AcceptRanges = t.AcceptRanges ? "bytes" : "none";
            try
            {
                await Response.Body.WriteAsync(t.First.AsMemory(0, t.FirstLength), ct).ConfigureAwait(false);
                await Response.Body.FlushAsync(ct).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                int n;
                while ((n = await t.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await Response.Body.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The viewer stopped watching.
            }
            catch (IOException)
            {
                // The connection closed.
            }
        }
    }

    private static string HeaderSafe(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        var clean = new string(line.Where(c => c >= ' ' && c < 127).ToArray());
        return clean.Length > 300 ? clean[..300] : clean;
    }

    /// <summary>
    /// The trailer as HLS (all qualities in one playlist), which Apple TV's player plays natively and
    /// starts within a second or two. Every playlist and segment is passed through this server.
    /// </summary>
    [HttpGet("Videos/{key}/master.m3u8")]
    public async Task<ActionResult> Hls([FromRoute] string key, CancellationToken ct)
    {
        if (!YtDlpService.IsValidKey(key))
        {
            return BadRequest();
        }

        var (meta, reason) = await _yt.GetHlsAsync(key, ct).ConfigureAwait(false);
        if (meta?.Hls is null || !Uri.TryCreate(meta.Hls, UriKind.Absolute, out var manifest))
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = reason ?? "No HLS version of this trailer." });
        }

        await ProxyAsync(manifest, meta.UserAgent, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>One playlist or segment of an HLS trailer, fetched from YouTube for the app (see <see cref="Hls"/>).</summary>
    [HttpGet("Videos/{key}/Proxy")]
    public async Task<ActionResult> Proxy([FromRoute] string key, [FromQuery] string? u, CancellationToken ct)
    {
        var target = HlsProxy.Decode(u);
        if (!YtDlpService.IsValidKey(key) || target is null)
        {
            return BadRequest();
        }

        var (meta, _) = await _yt.GetHlsAsync(key, ct).ConfigureAwait(false);
        await ProxyAsync(target, meta?.UserAgent, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    private async Task ProxyAsync(Uri target, string? userAgent, CancellationToken ct)
    {
        var client = _http.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, target);
        if (!string.IsNullOrEmpty(userAgent))
        {
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        if (Request.Headers.Range.Count > 0)
        {
            req.Headers.TryAddWithoutValidation("Range", Request.Headers.Range.ToString());
        }

        HttpResponseMessage res;
        try
        {
            res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            _yt.Fail(e.Message);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (res)
        {
            Response.StatusCode = (int)res.StatusCode;
            Response.Headers.CacheControl = "no-store";
            var type = res.Content.Headers.ContentType?.ToString();
            if (!res.IsSuccessStatusCode)
            {
                _yt.Fail($"YouTube answered {(int)res.StatusCode} for the trailer stream.");
                return;
            }

            try
            {
                if (HlsProxy.IsPlaylist(target, type))
                {
                    var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var token = Request.Query["api_key"].FirstOrDefault() ?? Request.Query["ApiKey"].FirstOrDefault();
                    Response.ContentType = "application/vnd.apple.mpegurl";
                    await Response.WriteAsync(HlsProxy.Rewrite(text, target, token), ct).ConfigureAwait(false);
                    _yt.Succeed();
                    return;
                }

                Response.ContentType = type ?? "application/octet-stream";
                if (res.Content.Headers.ContentLength is long len)
                {
                    Response.ContentLength = len;
                }

                if (res.Content.Headers.ContentRange is { } range)
                {
                    Response.Headers.ContentRange = range.ToString();
                }

                Response.Headers.AcceptRanges = "bytes";
                await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await body.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The viewer stopped watching.
            }
            catch (IOException)
            {
                // The connection closed.
            }
        }
    }

    /// <summary>Sets (or clears, with an empty key) the trailer used for an item. Administrators only.</summary>
    [HttpPost("Items/{itemId}/Override")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult Override([FromRoute] Guid itemId, [FromQuery] string? key)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var id = itemId.ToString("N");
        plugin.Configuration.Overrides.RemoveAll(o => string.Equals(o.ItemId, id, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(key))
        {
            var k = YtDlpService.IsValidKey(key) ? key : YtDlpService.KeyFromUrl(key);
            if (k is null)
            {
                return BadRequest("Not a YouTube link or video id.");
            }

            plugin.Configuration.Overrides.Add(new TrailerOverride { ItemId = id, YouTubeKey = k });
        }

        plugin.SaveConfiguration();
        return NoContent();
    }
}

/// <summary>Keys an admin can save for the calendar. Null leaves a key unchanged; empty clears it.</summary>
public class CalendarKeys
{
    public string? TraktClientId { get; set; }

    public string? TmdbApiKey { get; set; }
}
