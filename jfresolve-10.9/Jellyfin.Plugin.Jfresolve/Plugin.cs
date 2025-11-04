// plugin.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Jfresolve.Configuration;
using Jellyfin.Plugin.Jfresolve.SearchProviders;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jfresolve
{
    /// <summary>
    /// The main plugin class for Jfresolve.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
    {
        private readonly ILogger<Plugin> _logger;
        private System.Timers.Timer? _dailyTimer;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Jellyfin application paths.</param>
        /// <param name="xmlSerializer">XML serializer instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILoggerFactory loggerFactory)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = loggerFactory.CreateLogger<Plugin>();
            // Timer checks every 10 minutes
            _dailyTimer = new System.Timers.Timer(TimeSpan.FromMinutes(10).TotalMilliseconds);
            _dailyTimer.Elapsed += async (s, e) => await RunPopulationIfDueAsync().ConfigureAwait(false);
            _dailyTimer.AutoReset = true;
            _dailyTimer.Start();
        }

        /// <inheritdoc />
        public override string Name => "Jfresolve";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("506F18B8-5DAD-4CD3-B9A0-F7ED933E9939");

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Dispose managed resources
                if (_dailyTimer != null)
                {
                    _dailyTimer.Stop();
                    _dailyTimer.Dispose();
                    _dailyTimer = null;
                }
            }

            _disposed = true;
        }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
                }
            };
        }

        /// <summary>
        /// Triggers library population using the current configuration.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async System.Threading.Tasks.Task PopulateLibraryAsync()
        {
            if (Configuration == null)
            {
                _logger.LogWarning("Cannot populate anything: configuration is null.");
                return;
            }

            try
            {
                // Only populate media library if TMDb key is set
                if (!string.IsNullOrWhiteSpace(Configuration.TmdbApiKey))
                {
                    var mediaPopulator = new LibraryPopulator(Configuration, _logger);
                    await mediaPopulator.PopulateLibrariesAsync().ConfigureAwait(false);
                    _logger.LogInformation("Media library population completed successfully.");
                }
                else
                {
                    _logger.LogWarning("TMDb API key missing: skipping media library population.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during plugin operations");
            }
        }

        /// <summary>
        /// Run private async to populate media library.
        /// </summary>
        private async System.Threading.Tasks.Task RunPopulationIfDueAsync()
        {
            if (Configuration == null || !Configuration.EnableLibraryPopulation)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var targetHour = Configuration?.LibraryPopulationHour ?? 3;

            if (now.Hour == targetHour && (Configuration?.LastPopulationUtc?.Date != now.Date))
            {
                _logger.LogInformation("Starting daily media library population at {0:HH:mm} UTC (configured for hour {1})...", now, targetHour);
                await PopulateLibraryAsync().ConfigureAwait(false);
                if (Configuration != null)
                {
                    Configuration.LastPopulationUtc = now;
                    SaveConfiguration();
                }

                _logger.LogInformation("Daily media library population completed successfully at {0:HH:mm} UTC.", now);
            }
        }
    }
}
