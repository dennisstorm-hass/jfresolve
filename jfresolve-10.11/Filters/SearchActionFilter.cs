using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
                || (cad.ActionName != "GetItems" && cad.ActionName != "GetSearchHints"))
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
            // Only care about Movie and Series types - filter out everything else
            var requestedKinds = new HashSet<BaseItemKind>();
            var excludedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool hasEpisodeRequest = false;
            if (query.TryGetValue("IncludeItemTypes", out var includeVal))
            {
                foreach (var raw in includeVal.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Enum.TryParse<BaseItemKind>(raw, true, out var kind))
                    {
                        if (kind == BaseItemKind.Episode)
                        {
                            hasEpisodeRequest = true;
                        }
                        else if (kind == BaseItemKind.Movie || kind == BaseItemKind.Series)
                        {
                            requestedKinds.Add(kind);
                        }
                    }
                }
            }

            // If ONLY Episodes are requested (no Movie/Series), skip our filter
            if (hasEpisodeRequest && requestedKinds.Count == 0)
            {
                await next();
                return;
            }

            if (query.TryGetValue("ExcludeItemTypes", out var excludeVal))
            {
                excludedTypes.UnionWith(excludeVal.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries));
            }

            // If no Movie/Series types were requested, check if it's a global search
            bool isGlobalSearch = !query.ContainsKey("IncludeItemTypes") && !query.ContainsKey("ExcludeItemTypes");

            if (!isGlobalSearch && requestedKinds.Count == 0)
            {
                // Request is for other types only (not Movie/Series) - skip it
                await next();
                return;
            }

            // For global searches, include both Movie and Series
            if (isGlobalSearch)
            {
                requestedKinds.Add(BaseItemKind.Movie);
                requestedKinds.Add(BaseItemKind.Series);
            }

            var searchTerm = query["SearchTerm"].ToString();
            _logger.LogInformation("Jfresolve is intercepting search for: {SearchTerm} with types: {Types}", searchTerm, string.Join(",", requestedKinds));

            var allExternalResults = await _provider.SearchAsync(searchTerm);
            _logger.LogInformation("Jfresolve provider found {Count} total results for '{SearchTerm}'", allExternalResults.Count, searchTerm);

            // Convert to DTOs and group: Movies first, then Series
            var dtos = new List<BaseItemDto>();
            var options = new DtoOptions
            {
                Fields = new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.Overview },
                EnableImages = true,
            };

            // Add movies first if requested
            if (requestedKinds.Contains(BaseItemKind.Movie))
            {
                var movieResults = allExternalResults.Where(m => m.Type == "Movie").ToList();
                foreach (var meta in movieResults)
                {
                    var baseItem = _provider.IntoBaseItem(meta);
                    if (baseItem == null) continue;

                    _provider.MetaCache[baseItem.Id] = meta;
                    var dto = _dtoService.GetBaseItemDto(baseItem, options);
                    dtos.Add(dto);
                }
            }

            // Then add series if requested
            if (requestedKinds.Contains(BaseItemKind.Series))
            {
                var seriesResults = allExternalResults.Where(m => m.Type == "Series").ToList();
                foreach (var meta in seriesResults)
                {
                    var baseItem = _provider.IntoBaseItem(meta);
                    if (baseItem == null) continue;

                    _provider.MetaCache[baseItem.Id] = meta;
                    var dto = _dtoService.GetBaseItemDto(baseItem, options);
                    dtos.Add(dto);
                }
            }

            _logger.LogInformation("Successfully converted {Count} external items to DTOs. Movies: {Movies}, Shows: {Shows}",
                dtos.Count,
                dtos.Count(d => d.Type == BaseItemKind.Movie),
                dtos.Count(d => d.Type == BaseItemKind.Series));

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
