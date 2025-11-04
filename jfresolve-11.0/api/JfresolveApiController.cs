// JfresolveApiController.cs
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jfresolve;
using Jfresolve.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Api
{
    /// <summary>
    /// API controller for Jfresolve plugin endpoints.
    /// </summary>
    [Route("Plugins/Jfresolve")]
    [ApiController]
    public class PluginApiController : ControllerBase
    {
        private readonly ILogger<PluginApiController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginApiController"/> class.
        /// </summary>
        /// <param name="logger">The logger instance injected by Jellyfin.</param>
        public PluginApiController(ILogger<PluginApiController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Populates the media library asynchronously.
        /// </summary>
        /// <returns>Returns HTTP 200 OK on success, or 400 BadRequest if the plugin instance is unavailable.</returns>
        [HttpPost("PopulateLibrary")]
        public async Task<IActionResult> PopulateLibraryAsync()
        {
            if (JfresolvePlugin.Instance == null)
            {
                return BadRequest("Plugin instance not available");
            }

            try
            {
                _logger.LogInformation("[API] Manual library population triggered");
                await JfresolvePlugin.Instance.PopulateLibraryAsync().ConfigureAwait(false);
                _logger.LogInformation("[API] Manual library population completed");
                return Ok(new { success = true, message = "Library population started successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[API] Error during manual library population");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Resolves a stream dynamically from the configured addon.
        /// </summary>
        /// <param name="type">The content type (e.g. movie, series, anime).</param>
        /// <param name="id">The IMDb or Kitsu ID of the content.</param>
        /// <param name="season">Optional season number (for series).</param>
        /// <param name="episode">Optional episode number (for series).</param>
        /// <returns>
        /// Returns a redirect to the resolved stream URL, or an error if resolution fails.
        /// </returns>
        [HttpGet("resolve/{type}/{id}")]
        public async Task<IActionResult> ResolveStreamAsync(
            string type,
            string id,
            [FromQuery] string? season = null,
            [FromQuery] string? episode = null)
        {
            if (JfresolvePlugin.Instance == null)
            {
                return BadRequest("Plugin instance not available");
            }

            var config = JfresolvePlugin.Instance.Configuration;
            if (string.IsNullOrWhiteSpace(config.AddonManifestUrl))
            {
                return BadRequest("Addon manifest URL is not configured in the plugin settings.");
            }

            try
            {
                // Normalize manifest URL
                var manifestBase = UrlBuilder.NormalizeManifestUrl(config.AddonManifestUrl);

                // Build the correct stream endpoint for the Stremio addon
                string streamUrl;
                if (type.Equals("movie", StringComparison.OrdinalIgnoreCase))
                {
                    streamUrl = $"{manifestBase}/stream/movie/{id}.json";
                }
                else if (type.Equals("series", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(episode))
                    {
                        return BadRequest("Season and episode parameters are required for series type.");
                    }

                    streamUrl = $"{manifestBase}/stream/series/{id}:{season}:{episode}.json";
                }
                else
                {
                    streamUrl = $"{manifestBase}/stream/{type}/{id}.json";
                }

                _logger.LogInformation("Requesting stream from: {StreamUrl}", streamUrl);

                using var httpClient = new HttpClient();
                var response = await httpClient.GetStringAsync(streamUrl).ConfigureAwait(false);

                using var json = JsonDocument.Parse(response);
                if (!json.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                {
                    return NotFound($"No streams found for {id}");
                }

                var firstStream = streams[0];
                if (!firstStream.TryGetProperty("url", out var urlProperty))
                {
                    return NotFound("No stream URL available in response.");
                }

                var redirectUrl = urlProperty.GetString();
                if (string.IsNullOrWhiteSpace(redirectUrl))
                {
                    return NotFound("Empty stream URL received.");
                }

                _logger.LogInformation("Resolved {Type}/{Id} to {RedirectUrl}", type, id, redirectUrl);

                // ensure Jellyfin/FFmpeg follows the redirect correctly
                // Return 302 redirect with the proper absolute URL
                return RedirectPreserveMethod(redirectUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving stream for {Type}/{Id}", type, id);
                return StatusCode(500, $"Error resolving stream: {ex.Message}");
            }
        }
    }
}
