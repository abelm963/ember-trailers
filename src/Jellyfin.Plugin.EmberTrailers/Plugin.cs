using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.EmberTrailers;

/// <summary>
/// Streams official YouTube trailers through the Jellyfin server on demand, so apps
/// can play them in their own player without storing trailer files.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Ember Trailers";

    public override Guid Id => Guid.Parse("6f1c8f2e-6a43-4c4e-9d55-5e2a7b3c9e11");

    public override string Description =>
        "Plays official trailers in Ember and other apps by streaming them from YouTube on demand. Nothing is stored.";
}

public class TrailerOverride
{
    public string ItemId { get; set; } = string.Empty;

    public string YouTubeKey { get; set; } = string.Empty;
}

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the highest trailer resolution to stream.</summary>
    public int MaxHeight { get; set; } = 1080;

    /// <summary>Gets or sets a value indicating whether only trailers from verified channels play.</summary>
    public bool VerifiedOnly { get; set; } = true;

    /// <summary>Gets or sets an optional path to a yt-dlp binary; empty uses the one this plugin keeps updated.</summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>Gets or sets admin-chosen trailers that replace the automatic pick.</summary>
    public List<TrailerOverride> Overrides { get; set; } = new();

    /// <summary>Gets or sets an optional TMDB key for the release calendar; empty uses the one Jellyfin already has.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional Trakt client id; adds Trakt's most anticipated titles to the calendar. Stored on the server only.</summary>
    public string TraktClientId { get; set; } = string.Empty;
}

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<YtDlpService>();
        serviceCollection.AddSingleton<CalendarService>();
    }
}
