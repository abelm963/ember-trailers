# Ember Trailers

A Jellyfin plugin that streams official trailers to the [Ember](#) app through your own server. Nothing is downloaded or stored.

## Install

The easy way: open Ember on an admin profile and go to **Settings › Trailers › Set up trailer service**. Ember adds this repository, installs the plugin and restarts the server.

By hand: in Jellyfin go to **Dashboard › Plugins › Repositories**, add

```
https://raw.githubusercontent.com/abelm963/ember-trailers/main/manifest.json
```

then install **Ember Trailers** from the catalog and restart the server.

Works with Jellyfin 12.x and 10.11.x. The plugin downloads yt-dlp itself and keeps it updated (Dashboard › Scheduled Tasks › "Update trailer downloader").
