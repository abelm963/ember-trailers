using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EmberTrailers;

/// <summary>One dated thing on Ember's release calendar.</summary>
public sealed class CalendarEntry
{
    /// <summary>movie or tv.</summary>
    public string MediaType { get; set; } = "movie";

    public int TmdbId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>yyyy-MM-dd.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>cinema, digital (streaming / to buy — when it can be downloaded), series (new show) or season (new season).</summary>
    public string Kind { get; set; } = "cinema";

    public int? SeasonNumber { get; set; }

    /// <summary>For movies: the cinema and digital dates, whichever are known.</summary>
    public string? CinemaDate { get; set; }

    public string? DigitalDate { get; set; }

    public string? Overview { get; set; }

    public string? BackdropPath { get; set; }

    public string? PosterPath { get; set; }

    public List<string> Genres { get; set; } = new();

    public double Popularity { get; set; }
}

/// <summary>
/// Builds the release calendar from TMDB using the TMDB key Jellyfin already has (its TMDb plugin
/// setting, or Jellyfin's built-in key). Results are cached so every Ember on the server shares one fetch.
/// </summary>
public class CalendarService
{
    private static readonly int[] SkipTvGenres = { 10763, 10767, 10766 }; // news, talk, soap: new "seasons" constantly
    private readonly IHttpClientFactory _http;
    private readonly ILogger<CalendarService> _log;
    private readonly ConcurrentDictionary<string, (DateTime At, List<CalendarEntry> Items)> _cache = new();
    private readonly SemaphoreSlim _building = new(1, 1);
    private Dictionary<int, string>? _genreNames;

    public CalendarService(IHttpClientFactory http, ILogger<CalendarService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Used by tests only.</summary>
    internal string? KeyOverride { get; set; }

    /// <summary>Where the TMDB key came from (for the health check).</summary>
    public string KeySource { get; private set; } = "none";

    public string? LastError { get; private set; }

    /// <summary>Whether the last build also used Trakt.</summary>
    public bool TraktUsed { get; private set; }

    /// <summary>Forgets cached calendars (after the admin changes keys).</summary>
    public void Clear() => _cache.Clear();

    /// <summary>The TMDB key to use: this plugin's setting, then Jellyfin's TMDb plugin setting, then Jellyfin's built-in key.</summary>
    public string? ApiKey()
    {
        if (!string.IsNullOrEmpty(KeyOverride))
        {
            KeySource = "override";
            return KeyOverride;
        }

        var own = Plugin.Instance?.Configuration.TmdbApiKey;
        if (!string.IsNullOrWhiteSpace(own))
        {
            KeySource = "ember";
            return own.Trim();
        }

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MediaBrowser.Providers");
            var pluginType = asm?.GetType("MediaBrowser.Providers.Plugins.Tmdb.Plugin");
            var instance = pluginType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var config = instance?.GetType().GetProperty("Configuration")?.GetValue(instance);
            if (config?.GetType().GetProperty("TmdbApiKey")?.GetValue(config) is string custom && !string.IsNullOrWhiteSpace(custom))
            {
                KeySource = "jellyfin-custom";
                return custom.Trim();
            }

            var utils = asm?.GetType("MediaBrowser.Providers.Plugins.Tmdb.TmdbUtils");
            if (utils?.GetField("ApiKey", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is string builtIn && builtIn.Length > 0)
            {
                KeySource = "jellyfin-builtin";
                return builtIn;
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Ember: couldn't read Jellyfin's TMDB key");
        }

        KeySource = "none";
        return null;
    }

    public async Task<List<CalendarEntry>> GetAsync(string region, int days, CancellationToken ct)
    {
        region = string.IsNullOrWhiteSpace(region) || region.Length != 2 ? "US" : region.ToUpperInvariant();
        days = Math.Clamp(days, 7, 180);
        var cacheKey = $"{region}:{days}";
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromHours(6))
        {
            return hit.Items;
        }

        await _building.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(cacheKey, out hit) && DateTime.UtcNow - hit.At < TimeSpan.FromHours(6))
            {
                return hit.Items;
            }

            var items = await BuildAsync(region, days, ct).ConfigureAwait(false);
            _cache[cacheKey] = (DateTime.UtcNow, items);
            LastError = null;
            return items;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = e.Message;
            _log.LogWarning(e, "Ember: building the release calendar failed");
            if (hit.Items is not null)
            {
                return hit.Items;
            }

            throw;
        }
        finally
        {
            _building.Release();
        }
    }

    private async Task<List<CalendarEntry>> BuildAsync(string region, int days, CancellationToken ct)
    {
        var key = ApiKey() ?? throw new InvalidOperationException("No TMDB key found in Jellyfin.");
        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var bearer = key.Length > 40; // a v4 "read access token" rather than a v3 key

        async Task<JsonNode?> Get(string path, Dictionary<string, string>? q = null)
        {
            var query = new Dictionary<string, string>(q ?? new()) { ["language"] = "en-US" };
            if (!bearer)
            {
                query["api_key"] = key;
            }

            var url = "https://api.themoviedb.org/3" + path + "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            for (var attempt = 0; ; attempt++)
            {
                using var res = await client.SendAsync(req.Clone(), ct).ConfigureAwait(false);
                if ((int)res.StatusCode == 429 && attempt < 3)
                {
                    await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }

                if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }

                if ((int)res.StatusCode == 401)
                {
                    throw new InvalidOperationException("TMDB rejected Jellyfin's API key.");
                }

                res.EnsureSuccessStatusCode();
                return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
        }

        var today = DateTime.UtcNow.Date;
        var until = today.AddDays(days);
        var from = today.ToString("yyyy-MM-dd");
        var to = until.ToString("yyyy-MM-dd");
        _genreNames ??= await LoadGenres(Get).ConfigureAwait(false);

        // ---- Which titles to look at: TMDB's popular releases in the window, plus Trakt's most
        // anticipated lists when the admin has saved a Trakt key on the server.
        var movieIds = new HashSet<int>();
        var showIds = new HashSet<int>();
        async Task Collect(string path, HashSet<int> into, int pages, Dictionary<string, string> q)
        {
            for (var page = 1; page <= pages; page++)
            {
                var json = await Get(path, new(q) { ["page"] = page.ToString(), ["sort_by"] = "popularity.desc", ["include_adult"] = "false" })
                    .ConfigureAwait(false);
                var results = json?["results"]?.AsArray() ?? new JsonArray();
                foreach (var r in results)
                {
                    if ((r?["id"]?.GetValue<int>() ?? 0) is > 0 and var id)
                    {
                        into.Add(id);
                    }
                }

                if (results.Count < 20)
                {
                    break;
                }
            }
        }

        // Movies: release dates for this country, plus the US (TMDB's most complete dates).
        foreach (var country in new[] { region, "US" }.Distinct())
        {
            await Collect("/discover/movie", movieIds, 3, new()
            {
                ["region"] = country,
                ["with_release_type"] = "2|3",
                ["release_date.gte"] = from,
                ["release_date.lte"] = to,
            }).ConfigureAwait(false);
            await Collect("/discover/movie", movieIds, 2, new()
            {
                ["region"] = country,
                ["with_release_type"] = "4",
                ["release_date.gte"] = from,
                ["release_date.lte"] = to,
            }).ConfigureAwait(false);
        }

        // Shows: brand-new series, and popular shows with episodes airing (for new seasons).
        await Collect("/discover/tv", showIds, 3, new()
        {
            ["first_air_date.gte"] = from,
            ["first_air_date.lte"] = to,
            ["vote_count.gte"] = "0",
        }).ConfigureAwait(false);
        await Collect("/discover/tv", showIds, 8, new()
        {
            ["air_date.gte"] = from,
            ["air_date.lte"] = to,
            ["include_null_first_air_dates"] = "false",
        }).ConfigureAwait(false);

        var trakt = Plugin.Instance?.Configuration.TraktClientId?.Trim();
        TraktUsed = false;
        if (!string.IsNullOrEmpty(trakt))
        {
            try
            {
                foreach (var id in await TraktAnticipated(client, trakt, "movies", ct).ConfigureAwait(false))
                {
                    movieIds.Add(id);
                }

                foreach (var id in await TraktAnticipated(client, trakt, "shows", ct).ConfigureAwait(false))
                {
                    showIds.Add(id);
                }

                TraktUsed = true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Ember: Trakt lists unavailable, using TMDB only");
            }
        }

        var entries = new ConcurrentBag<CalendarEntry>();
        using var gate = new SemaphoreSlim(8);

        await Task.WhenAll(movieIds.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var m = await Get($"/movie/{id}", new() { ["append_to_response"] = "release_dates" }).ConfigureAwait(false);
                if (m is null || m["adult"]?.GetValue<bool>() == true)
                {
                    return;
                }

                var (cinema, digital) = PickDates(m["release_dates"], region);
                cinema ??= m["release_date"]?.GetValue<string>() is { Length: >= 10 } rd ? rd : null;
                CalendarEntry Make(string kind, string date) => new()
                {
                    MediaType = "movie",
                    TmdbId = id,
                    Title = m["title"]?.GetValue<string>() ?? string.Empty,
                    Date = date,
                    Kind = kind,
                    CinemaDate = cinema,
                    DigitalDate = digital,
                    Overview = m["overview"]?.GetValue<string>(),
                    BackdropPath = m["backdrop_path"]?.GetValue<string>(),
                    PosterPath = m["poster_path"]?.GetValue<string>(),
                    Genres = GenreNames(m),
                    Popularity = m["popularity"]?.GetValue<double>() ?? 0,
                };
                if (InWindow(cinema, today, until))
                {
                    entries.Add(Make("cinema", cinema!));
                }

                if (InWindow(digital, today, until))
                {
                    entries.Add(Make("digital", digital!));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogDebug(e, "Ember: skipped a movie on the calendar");
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        await Task.WhenAll(showIds.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var d = await Get($"/tv/{id}").ConfigureAwait(false);
                if (d is null || GenreIds(d).Any(g => SkipTvGenres.Contains(g)))
                {
                    return;
                }

                foreach (var season in d["seasons"]?.AsArray() ?? new JsonArray())
                {
                    var number = season?["season_number"]?.GetValue<int>() ?? 0;
                    var air = season?["air_date"]?.GetValue<string>();
                    if (number < 1 || !InWindow(air, today, until))
                    {
                        continue;
                    }

                    entries.Add(new CalendarEntry
                    {
                        MediaType = "tv",
                        TmdbId = id,
                        Title = d["name"]?.GetValue<string>() ?? string.Empty,
                        Date = air!,
                        Kind = number == 1 ? "series" : "season",
                        SeasonNumber = number,
                        Overview = (season?["overview"]?.GetValue<string>() is { Length: > 0 } so ? so : null) ?? d["overview"]?.GetValue<string>(),
                        BackdropPath = d["backdrop_path"]?.GetValue<string>(),
                        PosterPath = season?["poster_path"]?.GetValue<string>() ?? d["poster_path"]?.GetValue<string>(),
                        Genres = GenreNames(d),
                        Popularity = d["popularity"]?.GetValue<double>() ?? 0,
                    });
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogDebug(e, "Ember: skipped a show on the calendar");
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return entries
            .OrderBy(e => e.Date, StringComparer.Ordinal)
            .ThenByDescending(e => e.Popularity)
            .ToList();
    }

    private static bool InWindow(string? date, DateTime from, DateTime to) =>
        DateTime.TryParse(date, out var d) && d.Date >= from && d.Date <= to;

    /// <summary>Earliest cinema (types 2, 3) and digital (type 4) dates for the region, falling back to the US.</summary>
    private static (string? Cinema, string? Digital) PickDates(JsonNode? releaseDates, string region)
    {
        string? Find(string country, params int[] types)
        {
            var c = releaseDates?["results"]?.AsArray().FirstOrDefault(r => r?["iso_3166_1"]?.GetValue<string>() == country);
            return c?["release_dates"]?.AsArray()
                .Where(r => types.Contains(r?["type"]?.GetValue<int>() ?? 0))
                .Select(r => r?["release_date"]?.GetValue<string>())
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!.Length >= 10 ? s[..10] : s)
                .OrderBy(s => s, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        return (Find(region, 2, 3) ?? Find("US", 2, 3), Find(region, 4) ?? Find("US", 4));
    }

    /// <summary>Genre ids from either a list result (genre_ids) or a details result (genres).</summary>
    private static IEnumerable<int> GenreIds(JsonNode n) =>
        n["genre_ids"] is JsonArray ids
            ? ids.Select(g => g?.GetValue<int>() ?? 0)
            : (n["genres"]?.AsArray() ?? new JsonArray()).Select(g => g?["id"]?.GetValue<int>() ?? 0);

    private List<string> GenreNames(JsonNode n) =>
        GenreIds(n)
            .Select(g => _genreNames != null && _genreNames.TryGetValue(g, out var name) ? name : null)
            .Where(s => s != null)
            .Take(3)
            .ToList()!;

    /// <summary>TMDB ids from Trakt's "most anticipated" list (movies or shows).</summary>
    private static async Task<List<int>> TraktAnticipated(HttpClient client, string clientId, string kind, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/{kind}/anticipated?limit=100");
        req.Headers.Add("trakt-api-version", "2");
        req.Headers.Add("trakt-api-key", clientId);
        req.Headers.UserAgent.ParseAdd("EmberForJellyfin/0.2");
        using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
        if ((int)res.StatusCode is 401 or 403)
        {
            throw new InvalidOperationException("Trakt rejected the saved key.");
        }

        res.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var item = kind == "movies" ? "movie" : "show";
        return (json?.AsArray() ?? new JsonArray())
            .Select(x => x?[item]?["ids"]?["tmdb"]?.GetValue<int?>() ?? 0)
            .Where(id => id > 0)
            .ToList();
    }

    private static async Task<Dictionary<int, string>> LoadGenres(Func<string, Dictionary<string, string>?, Task<JsonNode?>> get)
    {
        var names = new Dictionary<int, string>();
        foreach (var path in new[] { "/genre/movie/list", "/genre/tv/list" })
        {
            var json = await get(path, null).ConfigureAwait(false);
            foreach (var g in json?["genres"]?.AsArray() ?? new JsonArray())
            {
                var id = g?["id"]?.GetValue<int>() ?? 0;
                var name = g?["name"]?.GetValue<string>();
                if (id > 0 && name != null)
                {
                    names[id] = name;
                }
            }
        }

        return names;
    }
}

internal static class HttpRequestExtensions
{
    /// <summary>HttpRequestMessage can only be sent once; retries need a copy.</summary>
    public static HttpRequestMessage Clone(this HttpRequestMessage r)
    {
        var copy = new HttpRequestMessage(r.Method, r.RequestUri);
        foreach (var h in r.Headers)
        {
            copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        return copy;
    }
}
