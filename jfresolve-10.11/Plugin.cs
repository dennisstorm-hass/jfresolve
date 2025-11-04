// JfresolvePlugin.cs
using System;
using System.Collections.Generic;
using Jfresolve;
using Jfresolve.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jfresolve
{
    /// <summary>
    /// Main plugin class for JF Resolve.
    /// </summary>
    public class JfresolvePlugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
    {
        private JfResolveManager? _manager;
        private ILogger<JfresolvePlugin>? _logger;
        private ILoggerFactory? _loggerFactory;
        private System.Timers.Timer? _dailyTimer;
        private bool _disposed;

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static JfresolvePlugin? Instance { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="JfresolvePlugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Application paths.</param>
        /// <param name="xmlSerializer">XML serializer.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        public JfresolvePlugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILoggerFactory loggerFactory)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<JfresolvePlugin>();
            _logger.LogInformation("[PLUGIN] jfresolve plugin initialized");

            // Initialize the daily timer for library population
            InitializeDailyTimer();
        }

        /// <summary>
        /// Initializes the daily timer for automatic library population.
        /// </summary>
        private void InitializeDailyTimer()
        {
            // Timer checks every 10 minutes
            _dailyTimer = new System.Timers.Timer(TimeSpan.FromMinutes(10).TotalMilliseconds);
            _dailyTimer.Elapsed += async (s, e) => await RunPopulationIfDueAsync().ConfigureAwait(false);
            _dailyTimer.AutoReset = true;
            _dailyTimer.Start();
            _logger?.LogInformation("[PLUGIN] Daily library population scheduler initialized");
        }

        /// <inheritdoc />
        public override string Name => "jfresolve";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("506F18B8-5DAD-4CD3-B9A0-F7ED933E9939");

        /// <inheritdoc />
        public override string Description => "External results and on-demand sources integration for Jellyfin.";

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            var prefix = GetType().Namespace;
            yield return new PluginPageInfo
            {
                Name = "config",
                EmbeddedResourcePath = prefix + ".Config.config.html",
            };
        }

        /// <inheritdoc />
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            _logger?.LogInformation("[PLUGIN] Configuration updated - Library population hour set to {Hour} UTC", Configuration?.LibraryPopulationHour ?? 3);

            // Manager will be recreated by DI when needed
            _manager?.Dispose();
            _manager = null;
        }

        /// <summary>
        /// Checks if library population should run at the configured time.
        /// Runs once per day at the configured hour.
        /// </summary>
        private async System.Threading.Tasks.Task RunPopulationIfDueAsync()
        {
            if (Configuration == null || !Configuration.EnableLibraryPopulation)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var targetHour = Configuration.LibraryPopulationHour;

            if (now.Hour == targetHour && (Configuration.LastPopulationUtc?.Date != now.Date))
            {
                _logger?.LogInformation("[PLUGIN] Starting daily library population at {Time:HH:mm} UTC (configured for hour {Hour})...", now, targetHour);
                await PopulateLibraryAsync().ConfigureAwait(false);
                Configuration.LastPopulationUtc = now;
                SaveConfiguration();
                _logger?.LogInformation("[PLUGIN] Daily library population completed successfully at {Time:HH:mm} UTC.", now);
            }
        }

        /// <summary>
        /// Manually triggers library population.
        /// Can be called from API endpoints.
        /// </summary>
        public async System.Threading.Tasks.Task PopulateLibraryAsync()
        {
            if (Configuration == null)
            {
                _logger?.LogError("[PLUGIN] Plugin configuration not available, cannot populate library");
                return;
            }

            try
            {
                _logger?.LogInformation("[PLUGIN] Starting manual library population with ItemsPerRequest={ItemsPerRequest}", Configuration.ItemsPerRequest);

                // Create manager on-demand for library population
                // Use the plugin's logger factory to create a logger for the manager
                var managerLogger = _loggerFactory?.CreateLogger("JfResolveManager")
                    ?? NullLogger.Instance;
                using var manager = new JfResolveManager(Configuration, managerLogger);
                await manager.PopulateLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PLUGIN] Error during manual library population");
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Stop and dispose the daily timer
                if (_dailyTimer != null)
                {
                    _dailyTimer.Stop();
                    _dailyTimer.Dispose();
                    _dailyTimer = null;
                }

                _manager?.Dispose();
                _logger?.LogInformation("[PLUGIN] jfresolve plugin disposed");
            }

            _disposed = true;
        }

        /// <inheritdoc />
        public IPluginServiceRegistrator ServiceRegistrator => new ServiceRegistrator();
    }
}
