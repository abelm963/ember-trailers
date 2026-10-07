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

    public TrailerController(YtDlpService yt, ILibraryManager library, CalendarService calendar)
    {
        _yt = yt;
        _library = library;
        _calendar = calendar;
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
            features = new[] { "trailers", "calendar", "reasons", "streamtest" },
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

    private async Task<VideoMeta?> Check(string key, bool trusted, CancellationToken ct)
    {
        var meta = await _yt.GetMetaAsync(key, ct).ConfigureAwait(false);
        if (meta is null || !meta.Playable)
        {
            return null;
        }

        var verifiedOnly = Plugin.Instance?.Configuration.VerifiedOnly ?? true;
        if (verifiedOnly && !trusted && !meta.Verified)
        {
            return null;
        }

        return meta;
    }

    /// <summary>
    /// Admin diagnostics: runs the real streaming pipeline for up to 40 seconds and reports how it went.
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
        using var p = await _yt.StartStreamAsync(key, ct).ConfigureAwait(false);
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var buffer = new byte[64 * 1024];
        long total = 0;
        long? firstByteMs = null;
        byte first = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            int n;
            while (total < 8_000_000 && (n = await p.StandardOutput.BaseStream.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (firstByteMs is null)
                {
                    firstByteMs = sw.ElapsedMilliseconds;
                    first = buffer[0];
                }

                total += n;
            }
        }
        catch (OperationCanceledException)
        {
        }

        var seconds = sw.Elapsed.TotalSeconds;
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

        var err = string.Empty;
        try
        {
            err = await errTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        var lines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new
        {
            ok = total > 500_000,
            stage = "stream",
            infoMs,
            firstByteMs,
            bytes = total,
            mbps = seconds > 0 ? Math.Round(total * 8 / seconds / 1_000_000, 1) : 0,
            container = firstByteMs is null ? null : first == 0x47 ? "mpegts" : "matroska",
            deno = _yt.HasDeno,
            channel = meta.Channel,
            error = lines.LastOrDefault(),
        };
    }

    /// <summary>Streams the trailer while it is fetched. Nothing is written to disk.</summary>
    [HttpGet("Videos/{key}/Stream")]
    public async Task Stream([FromRoute] string key, CancellationToken ct)
    {
        if (!YtDlpService.IsValidKey(key))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        Process p;
        try
        {
            p = await _yt.StartStreamAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _yt.Fail(e.Message);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (p)
        {
            var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
            var buffer = new byte[64 * 1024];
            var stdout = p.StandardOutput.BaseStream;
            var started = false;
            try
            {
                int n;
                while ((n = await stdout.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (!started)
                    {
                        started = true;
                        // HLS arrives as MPEG-TS (starts with 0x47), joined streams as Matroska.
                        Response.ContentType = buffer[0] == 0x47 ? "video/mp2t" : "video/x-matroska";
                        Response.Headers.CacheControl = "no-store";
                        _yt.Succeed();
                    }

                    await Response.Body.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }

                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                if (!started)
                {
                    _yt.Fail(await errTask.ConfigureAwait(false));
                    Response.StatusCode = StatusCodes.Status502BadGateway;
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
            finally
            {
                if (!p.HasExited)
                {
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
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
