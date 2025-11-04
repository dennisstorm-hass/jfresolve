using Jellyfin.Plugin.Jfresolve.SearchProviders;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jfresolve
{
    /// <summary>
    /// Registers Jfresolve services with the Jellyfin dependency injection system.
    /// </summary>
    public class JfResolveServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<JfResolveSearchProvider>();
            serviceCollection.Configure<MvcOptions>(options =>
            {
                options.Filters.AddService<JfResolveSearchProvider>();
            });
        }
    }
}
