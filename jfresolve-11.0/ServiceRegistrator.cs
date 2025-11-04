using System;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Filters;
using Jfresolve.Provider;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jfresolve
{
    /// <summary>
    /// Registers services and filters for the Jfresolve plugin.
    /// </summary>
    public class ServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            // Register HttpClientFactory for TMDB API calls
            services.AddHttpClient(nameof(JfresolveProvider))
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                });

            // Register the JfresolveProvider
            services.AddSingleton<JfresolveProvider>();

            // Register the STRM file generator
            services.AddSingleton<StrmFileGenerator>();

            // Register the filters as services
            services.AddSingleton<SearchActionFilter>();
            services.AddSingleton<InsertActionFilter>();
            services.AddSingleton<ImageResourceFilter>();
            services.AddSingleton<PlaybackInfoFilter>();

            // Register the FFmpeg configuration hosted service
            services.AddHostedService<FFmpegConfigSetter>();

            // Add the filters to the MVC pipeline
            services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
            {
                o.Filters.AddService<PlaybackInfoFilter>();
                o.Filters.AddService<SearchActionFilter>();
                o.Filters.AddService<InsertActionFilter>();
                o.Filters.AddService<ImageResourceFilter>();
            });
        }
    }

    /// <summary>
    /// Configures FFmpeg settings during plugin startup.
    /// Sets probe size and analyze duration for better remote stream detection.
    /// This is especially important for streaming from torrent providers and other remote sources.
    /// </summary>
    public class FFmpegConfigSetter : IHostedService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<FFmpegConfigSetter> _logger;

        public FFmpegConfigSetter(IConfiguration config, ILogger<FFmpegConfigSetter> logger)
        {
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Applies FFmpeg configuration settings when the service starts.
        /// Respects the EnableFFmpegCustomization toggle - if disabled, Jellyfin's default FFmpeg configuration is used.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Check if FFmpeg customization is enabled
                var enableFFmpegCustomization = JfresolvePlugin.Instance?.Configuration?.EnableFFmpegCustomization ?? true;

                if (enableFFmpegCustomization)
                {
                    // Get FFmpeg settings from plugin configuration, with sensible defaults
                    var probeSize = JfresolvePlugin.Instance?.Configuration?.FFmpegProbeSize ?? "40M";
                    var analyzeDuration = JfresolvePlugin.Instance?.Configuration?.FFmpegAnalyzeDuration ?? "5M";

                    // Apply settings to Jellyfin's FFmpeg configuration
                    _config["FFmpeg:probesize"] = probeSize;
                    _config["FFmpeg:analyzeduration"] = analyzeDuration;

                    _logger.LogInformation(
                        "[Jfresolve] FFmpeg configuration applied: probesize={ProbeSize}, analyzeduration={AnalyzeDuration}",
                        probeSize,
                        analyzeDuration);
                }
                else
                {
                    _logger.LogInformation("[Jfresolve] FFmpeg customization is disabled, using Jellyfin's default FFmpeg configuration");
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Jfresolve] Error configuring FFmpeg settings");
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Called when the hosted service is stopping.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
