<p align="center">
  <img src="https://raw.githubusercontent.com/dennisstorm-hass/jfresolve/main/jfresolve.png" alt="Jfresolve Logo" width="128" height="128">
</p>

<h1 align="center">Jfresolve - Jellyfin Plugin</h1>

A Jellyfin plugin that integrates external streaming sources (Stremio addons) from a debrid provider with your Jellyfin library, enabling on-demand content discovery and streaming.

<p align="center">
  <a href="https://raw.githubusercontent.com/dennisstorm-hass/jfresolve/main/repository.json" target="_blank">
    <img src="https://img.shields.io/badge/Add%20to%20Jellyfin-13B5EA?style=for-the-badge&logo=jellyfin&logoColor=white" alt="Jellyfin Repo">
  </a>
</p>

> **Note**: This is a maintained fork of the original [Jfresolve](https://github.com/vicking20/jfresolve) plugin by [vicking20](https://github.com/vicking20). This fork includes compatibility fixes for Jellyfin 10.11.6 and improvements to streaming functionality.

Similar project: If you aren't interested in using the plugin, you can instead use the webapp: [**JF-Resolve**](https://github.com/vicking20/jf-resolve) directly with Jellyfin.

## Features

- **External Search Results**: Search TMDB and display results from external streaming sources.
- **Library Population**: Automatically populate your Jellyfin library with popular and trending content from TMDB.
- **Movie & Series Support**: Full support for movies, TV series, and anime.
- **Anime Categorization**: Optional dedicated anime library with automatic genre-based routing.
- **FFmpeg Tuning**: Configurable FFmpeg settings for better remote stream detection.
- **Scheduled Tasks**: Automate library population, series updates, and content purging.
- **Flexible Configuration**: Comprehensive settings for customization.
- **Preferred Quality Selection**: Choose your preferred stream quality (4K, 1080p, etc.) and the plugin will automatically select the best stream for you.
- **Failover System**: EXPERIMENTAL: Automatic retry for dead links with configurable time windows - prevents repeated failures on the same dead stream.

## Benefits

- **Efficient Library Management**: Simplify your media library with automated population and external search results.
- **Enhanced User Experience**: Discover new content directly through your Jellyfin UI.
- **Customizable Settings**: Tailor your plugin to your preferences with flexible configuration options.
- **Less dependency on the arr stack**: You can use jfresolve now without jellyseerr, radarr, sonarr, prowlarr,etc. Just your tmdb api key, debrid authentication, and some stremio streaming addon manifest link.
- **Smaller file footprint**: Media is not stored directly, media is streamed from the source, you dont need to have tons of storage to have a large library.

## Versions

### **jfresolve-10.12** (Current)
- **Target**: Jellyfin 10.11.0+ (Tested and compatible with Jellyfin 10.11.6)
- **Framework**: .NET 9.0
- **Status**: Active development
- **Latest Release**: See [Releases](https://github.com/dennisstorm-hass/jfresolve/releases)

### Recent Changes (v1.0.0.12+)

- **Jellyfin 10.11.6 Compatibility**: Fixed interface implementation issues with `IItemRepository.ReattachUserDataAsync` method
- **Streaming Improvements**: Enhanced HTTP range request support (206 Partial Content) for FFmpeg seeking
- **Stream Proxying**: Implemented proper stream proxying instead of redirects for better compatibility with Jellyfin 10.11.6
- **Header Handling**: Fixed Content-Range header handling to prevent streaming errors
- **NuGet Packages**: Updated to Jellyfin 10.11.6 packages for proper interface compatibility

## Installation

1. Add the link to the plugin to your Jellyfin server's plugin repository: 
   ```
   https://raw.githubusercontent.com/dennisstorm-hass/jfresolve/main/repository.json
   ```
2. Install and configure your plugin. Tested with Torrentio, TorrentioRD, Aiostreams, MediaFusion. Your plugin needs to have your real debrid key setup.
3. During the first time configuration or after adding a new library path, after saving your settings, you should restart Jellyfin, then trigger a library refresh for changes to take effect.
4. If you have used an older version of Jfresolve older than 1.0.0.3, you need to uninstall the older version.

### Jellyfin 10.11.6 Users

This fork is specifically tested and compatible with Jellyfin 10.11.6. If you're experiencing issues with the original plugin on Jellyfin 10.11.6, this fork includes the necessary compatibility fixes.
## Configuration

### Required Settings

- **TMDb API Key**: Get your free API key at [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api).
- **Jellyfin Base URL**: Your Jellyfin server URL (e.g., `http://127.0.0.1:8096`).
- **Addon Manifest URL**: Stremio addon manifest URL (required for streaming).
  **Sample Add-on url** ```stremio://torrentio.strem.fun/providers=yts,eztv,rarbg,1337x,thepiratebay,kickasstorrents,torrentgalaxy,magnetdl,horriblesubs,nyaasi,tokyotosho,anidex|qualityfilter=brremux,scr,cam|limit=1|debridoptions=nodownloadlinks,nocatalog|realdebrid=(input your real debrid key here with no brackets)/manifest.json```
  The sample add-on url can be used in your configuration, replace **(input your real debrid key here with no brackets)** with your real debrid key and paste into the plugin settings Addon link (Manifest JSON URL).
- **Debrid Account**: Tested with Real Debrid, other providers can be tested. Debrid provider is configured in your stremio plugin settings.
- **Library Paths**: At least one library path (Movies or Shows).

### Optional Settings

- **Enable Search Interception**: When enabled, search queries will return results from external search provider.
- **Preferred Stream Quality**: Select preferred quality when multiple stream options are available. Auto will select the highest quality stream.
- **Search Result Limit**: Maximum number of results to return from TMDB searches.
- **Unreleased Buffer Days**: Number of days before official release date to consider content as "released".
- **Enable Separate Anime Folder**: When enabled, anime shows (TMDB genre ID 16) will be added to a separate anime folder instead of the main series folder.
- **Enable Auto Library Population**: Automatically populate your library with trending/popular/top rated content.
- **Items Per Run**: Maximum number of new items to add each time the population task runs.
- **Enable Custom FFmpeg Settings**: When disabled, Jellyfin's default FFmpeg settings will be used. Enable this to customize probe and analyze settings for better stream detection.
- **Enable Movie Failover**: Automatically retry failed movie streams with alternative quality versions.
- **Enable Show Failover**: Automatically retry failed TV show streams with alternative quality versions.
- **Failover Grace Period**: Time in seconds to wait before retrying a failed stream (prevents immediate retry spam).
- **Failover Window**: Time in seconds during which a failed stream won't be retried again (prevents continuous failures).

## Scheduled Tasks

Jfresolve comes with three scheduled tasks to automate your library management:

- **Populate Jfresolve Library**: Automatically populates your library with trending/popular content from TMDB.
- **Update Jfresolve Series**: Keeps your TV series up-to-date with new seasons/episodes.
- **Clear All Jfresolve Items**: Removes all items added by Jfresolve from your library.

To configure the scheduled tasks, go to **Dashboard → Scheduled Tasks** in your Jellyfin server.

## Building from Source

### Prerequisites

- .NET 9.0 SDK

### Build Steps

```bash
cd jfresolve-10.11
dotnet build -c Release
```

Compiled DLL will be at:
- `jfresolve-10.11/bin/Release/net9.0/Jfresolve.dll`

## Troubleshooting

### Plugin doesn't show in Dashboard

- Ensure correct version for your Jellyfin release.
- Check plugin folder permissions.
- Restart Jellyfin after installation.

### Search returns no results

- Verify TMDb API key is valid.
- Check internet connectivity.
- Enable **Enable Search Interception** in config.

### Library population not working

- Check TMDb API key configuration.
- Verify library paths exist and are in Jellyfin.
- Check logs for specific error messages.
- Ensure at least one library path is configured.

### Streams not playing

- Verify addon manifest URL is correct.
- Check Jellyfin FFmpeg configuration.
- Ensure Debrid is authorized in your plugin.
- Check Jellyfin logs for stream resolution errors.
- For Jellyfin 10.11.6: Ensure you're using version 1.0.0.11 or later which includes compatibility fixes.

### Kodi / JellyCon: playback drops every ~10 minutes (web player is fine)

Playback via [JellyCon](https://github.com/jellyfin/jellycon) (Kodi) can stop after about 10 minutes while the same stream works in the Jellyfin web player. JellyCon does not implement custom streaming; it passes the play URL from Jellyfin to Kodi via `list_item.setPath(playurl)`, and Kodi’s player reads that URL. The difference is how Kodi handles the HTTP connection (e.g. timeouts or closing the connection) versus the browser. This plugin mitigates that by:

- **Transparent reconnect**: If the upstream (e.g. Real-Debrid) or the connection drops mid-stream, the plugin reconnects from the current byte offset and continues streaming so the client sees one continuous stream.
- **Resume-friendly responses**: The plugin sends `Content-Length` and supports `Range` so Kodi can resume with `Range: bytes=X-` when it reconnects.
- **Clean disconnect handling**: On client disconnect the connection is aborted so the server does not log “Content-Length mismatch” errors.

If dropouts persist, try the latest plugin version and check Jellyfin logs for “Reconnected at byte” messages (reconnect is working) or “Stream ended after upstream failure” (upstream or reconnect limit).

### Plugin fails to load in Jellyfin 10.11.6

- Ensure you're using version 1.0.0.11 or later of this fork.
- The original plugin may not work with Jellyfin 10.11.6 due to interface changes.
- Check Jellyfin logs for "does not have an implementation" errors - this indicates you need the updated version.

## Contributing

Contributions are welcome! Please:

1. Fork the repository.
2. Create a feature branch.
3. Commit changes.
4. Push to branch.
5. Open a Pull Request.

## Acknowledgments

- **Original Developer**: [vicking20](https://github.com/vicking20) - Original creator of Jfresolve
- This plugin was only possible after going through [Gelato](https://github.com/lostb1t/Gelato). Big thanks to [lostb1t](https://github.com/lostb1t).
- Original project [jf-resolve](https://github.com/vicking20/jf-resolve).
- Jellyfin project for the media server.
- TMDB for metadata.
- Stremio for addon ecosystem.
- All users and contributors.

## Support

- **Issues**: Report bugs on [GitHub Issues](https://github.com/dennisstorm-hass/jfresolve/issues).
- **Discussions**: Ask questions in [GitHub Discussions](https://github.com/dennisstorm-hass/jfresolve/discussions).
- **Original Repository**: For the original plugin, visit [vicking20/jfresolve](https://github.com/vicking20/jfresolve).
- **Jellyfin Forum**: Check the [Jellyfin Community](https://jellyfin.org/docs/general/community/).

## Disclaimer

This project is intended for **educational purposes only**. The original plugin was developed to learn more about the c# language and understand how to inject custom results when a user does a search in Jellyfin using Dto's. While it was a fun experiment, it is provided as-is, and others are welcome to modify or use it for their own educational purposes at their risk.

This fork maintains the same educational purpose while providing compatibility fixes for newer Jellyfin versions.
