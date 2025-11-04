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
            _logger.LogInformation("[PLUGIN] JF Resolve plugin initialized");

            // Manager will be created by ServiceRegistrator with full DI
            // This is just for scheduler initialization if needed later
        }

        /// <inheritdoc />
        public override string Name => "JF Resolve";

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
            _logger?.LogInformation("[PLUGIN] Configuration updated");

            // Manager will be recreated by DI when needed
            _manager?.Dispose();
            _manager = null;
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
                _manager?.Dispose();
                _logger?.LogInformation("[PLUGIN] JF Resolve plugin disposed");
            }

            _disposed = true;
        }

        /// <inheritdoc />
        public IPluginServiceRegistrator ServiceRegistrator => new ServiceRegistrator();
    }
}
