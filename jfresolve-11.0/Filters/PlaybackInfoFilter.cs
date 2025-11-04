using System;
using System.Globalization;
using System.Threading.Tasks;
using Jfresolve.Provider;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters
{
    /// <summary>
    /// Intercepts GetPlaybackInfo requests to provide streaming sources for virtual Jfresolve items.
    /// When a user clicks play on a TMDB search result (cached/virtual item), this filter
    /// redirects directly to the resolver endpoint for immediate playback.
    /// </summary>
    public class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
    {
        private readonly ILogger<PlaybackInfoFilter> _logger;
        private readonly JfresolveProvider _provider;

        public int Order => 0; // Run early, before other filters

        public PlaybackInfoFilter(ILogger<PlaybackInfoFilter> logger, JfresolveProvider provider)
        {
            _logger = logger;
            _provider = provider;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
        {
            // Only handle GetPlaybackInfo actions
            if (!IsPlaybackInfoAction(ctx))
            {
                await next();
                return;
            }

            // Try to get the item ID from route
            if (!TryGetItemId(ctx, out var itemId))
            {
                await next();
                return;
            }

            // Check if this is a cached virtual item from our provider
            if (!_provider.MetaCache.TryGetValue(itemId, out var meta) || meta == null)
            {
                // Not our item, proceed normally
                await next();
                return;
            }

            _logger.LogInformation("[PlaybackInfoFilter] Intercepting playback for cached item: {Name}", meta.Name);

            // Check for provider IDs
            var imdbId = meta.ImdbId;
            if (string.IsNullOrEmpty(imdbId))
            {
                _logger.LogWarning("[PlaybackInfoFilter] Item {Name} has no IMDB ID, cannot provide stream", meta.Name);
                await next();
                return;
            }

            // Redirect directly to resolver endpoint
            var resolverType = meta.Type == "Series" ? "series" : "movie";
            var resolverPath = $"/Plugins/Jfresolve/resolve/{resolverType}/{imdbId}";

            _logger.LogInformation("[PlaybackInfoFilter] Redirecting to resolver: {Path}", resolverPath);

            // Return redirect to the resolver endpoint
            ctx.Result = new RedirectResult(resolverPath, permanent: false);
        }

        /// <summary>
        /// Checks if this is a GetPlaybackInfo action.
        /// </summary>
        private bool IsPlaybackInfoAction(ActionExecutingContext ctx)
        {
            if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
                return false;

            return string.Equals(cad.ActionName, "GetPlaybackInfo", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Tries to extract the item ID from route parameters.
        /// </summary>
        private bool TryGetItemId(ActionExecutingContext ctx, out Guid itemId)
        {
            itemId = Guid.Empty;

            // Try to get from route data
            var rd = ctx.RouteData.Values;
            foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
            {
                if (rd.TryGetValue(key, out var raw) && raw is not null)
                {
                    var s = raw.ToString();
                    if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out var g))
                    {
                        itemId = g;
                        return true;
                    }
                }
            }

            // Try to get from action arguments
            if (ctx.ActionArguments.TryGetValue("itemId", out var arg) && arg is Guid argGuid)
            {
                itemId = argGuid;
                return true;
            }

            return false;
        }
    }
}
