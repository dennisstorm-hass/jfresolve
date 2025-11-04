using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jfresolve.Configuration;
using Jfresolve.Provider;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters
{
    public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
    {
        private readonly ILogger<SearchActionFilter> _logger;
        private readonly JfresolveProvider _provider;
        private readonly IDtoService _dtoService;

        // Update constructor
        public SearchActionFilter(ILogger<SearchActionFilter> logger, JfresolveProvider provider, IDtoService dtoService)
        {
            _logger = logger;
            _provider = provider;
            _dtoService = dtoService;
        }

        public int Order => 1;

        public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
        {
            if (ctx.ActionDescriptor is not Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor cad
                || cad.ActionName != "GetItems")
            {
                await next();
                return;
            }

            var config = JfresolvePlugin.Instance?.Configuration;
            if (config?.EnableMixed != true)
            {
                await next();
                return;
            }

            var query = ctx.HttpContext.Request.Query;
            var hasSearch = query.Keys.Any(k =>
                string.Equals(k, "SearchTerm", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(query[k])
            );

            if (!hasSearch)
            {
                await next();
                return;
            }

            // Figure out what types of items Jellyfin is searching for
            var requestedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (query.TryGetValue("IncludeItemTypes", out var includeVal))
            {
                requestedTypes.UnionWith(includeVal.ToString().Split(','));
            }

            var supportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Movie", "Series", "BoxSet" };
            bool isSupportedSearch = requestedTypes.Count > 0 && requestedTypes.IsSubsetOf(supportedTypes);
            bool isGlobalSearch = requestedTypes.Count == 0;

            // If the search is not a global search, and not exclusively for types we support, then we ignore it.
            if (!isSupportedSearch && !isGlobalSearch)
            {
                await next();
                return;
            }

            var searchTerm = query["SearchTerm"].ToString();
            _logger.LogInformation("Jfresolve is intercepting search for: {SearchTerm} with types: {Types}", searchTerm, string.Join(",", requestedTypes));

            var externalResults = await _provider.SearchAsync(searchTerm);
            _logger.LogInformation("Jfresolve provider found {Count} total results for '{SearchTerm}'", externalResults.Count, searchTerm);

            var filteredResults = externalResults.Where(meta => {
                // If no types were specified, we are in a global search, so include everything.
                if (requestedTypes.Count == 0) return true;
                // Otherwise, only include the item if its type was requested.
                return requestedTypes.Contains(meta.Type);
            }).ToList();

            // This is where we convert the external results to DTOs
            var dtos = new List<BaseItemDto>();
            var options = new DtoOptions
            {
                Fields = new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.Overview },
                EnableImages = true,
            };

            foreach (var meta in filteredResults)
            {
                var baseItem = _provider.IntoBaseItem(meta);
                if (baseItem == null)
                {
                    continue;
                }

                // Cache the metadata so the image filter can find it later
                _provider.MetaCache[baseItem.Id] = meta;

                var dto = _dtoService.GetBaseItemDto(baseItem, options);
                dtos.Add(dto);
            }

            _logger.LogInformation("Successfully converted {Count} external items to DTOs.", dtos.Count);

            ctx.Result = new OkObjectResult(
                new QueryResult<BaseItemDto>
                {
                    Items = dtos.ToArray(),
                    TotalRecordCount = dtos.Count,
                }
            );
            return;
        }
    }
}
