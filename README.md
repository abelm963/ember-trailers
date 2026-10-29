# Ember Trailers

A Jellyfin plugin for the Ember app:

- **Trailers** – streams official YouTube trailers through your own server. Nothing is downloaded or stored.
- **Coming Soon calendar** – movies (cinema and digital release dates), new series and new seasons for the next few months, from TMDB using the key your Jellyfin already has. Add a Trakt Client ID (in Ember, admin profile) to include Trakt's most anticipated titles.

API keys are kept in the plugin's settings on your server only; they are never part of the app or this repository.

## Install

The easy way: open Ember on an admin profile and go to **Settings › Trailers › Set up trailer service**. Ember adds this repository, installs the plugin and restarts the server.

By hand: in Jellyfin go to **Dashboard › Plugins › Repositories**, add

```
https://raw.githubusercontent.com/abelm963/ember-trailers/main/manifest.json
```

then install **Ember Trailers** from the catalog and restart the server.

**After installing or updating, fully restart Jellyfin** (restart the Docker container or the Jellyfin service). Jellyfin's own Restart button in the dashboard does not load new plugin code.

Works with Jellyfin 12.x and 10.11.x.

## How trailers are streamed

yt-dlp looks the video up once (when Ember opens a title page), and the server then reads YouTube's streams directly: YouTube's ready-made HD stream first, then separate picture and sound joined by Jellyfin's own ffmpeg, then a plain file, then yt-dlp downloading it itself. If YouTube refuses one way, the next is tried. When none works, the stream answers 502 with the reason (`X-Ember-Error` header and a JSON body), and admins can see every attempt with `GET /EmberTrailers/Videos/{key}/Test`.

Apple TV plays YouTube's own HLS stream instead, through `Videos/{key}/master.m3u8` (every playlist and segment is passed through the server). The plugin downloads yt-dlp itself and keeps it updated (Dashboard › Scheduled Tasks › "Update trailer downloader").
