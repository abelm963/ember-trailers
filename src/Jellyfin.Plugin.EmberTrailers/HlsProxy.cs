using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EmberTrailers;

/// <summary>
/// Passes YouTube's own HLS trailer streams through the server. Apple TV's player plays HLS natively
/// and starts it within a second or two, but YouTube's addresses only work from the address that
/// asked for them (the server), so every playlist and segment is fetched by the server for the app.
/// Only YouTube's video hosts can be reached this way.
/// </summary>
public static partial class HlsProxy
{
    [GeneratedRegex("URI=\"([^\"]+)\"")]
    private static partial Regex UriAttribute();

    /// <summary>Whether the server may fetch this address for an app (YouTube's video and manifest hosts only).</summary>
    public static bool IsAllowed(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase));

    public static string Encode(string url) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Uri? Decode(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var b64 = token.Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - (b64.Length % 4)) % 4);
        try
        {
            var url = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && IsAllowed(u) ? u : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Points every address in a playlist at the proxy. The result is served from
    /// <c>…/Videos/{key}/</c>, so a relative <c>Proxy?u=…</c> resolves to the proxy endpoint.
    /// The sign-in token is repeated on each address because players don't carry query strings over.
    /// </summary>
    public static string Rewrite(string playlist, Uri source, string? token)
    {
        var auth = string.IsNullOrEmpty(token) ? string.Empty : "&api_key=" + Uri.EscapeDataString(token);
        string Proxied(string address)
        {
            if (!Uri.TryCreate(source, address, out var abs) || !IsAllowed(abs))
            {
                return address;
            }

            return "Proxy?u=" + Encode(abs.ToString()) + auth;
        }

        var sb = new StringBuilder(playlist.Length + 1024);
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }

            if (line[0] == '#')
            {
                sb.Append(UriAttribute().Replace(line, m => "URI=\"" + Proxied(m.Groups[1].Value) + "\""));
            }
            else
            {
                sb.Append(Proxied(line.Trim()));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    public static bool IsPlaylist(Uri u, string? contentType) =>
        (contentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false) ||
        u.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        u.Host.StartsWith("manifest.", StringComparison.OrdinalIgnoreCase);
}
