using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jfresolve;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters;

/// <summary>
/// Intercepts search requests and returns TMDB results (based on Gelato's SearchActionFilter pattern)
/// </summary>
public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly IDtoService _dtoService;
    private readonly JfresolveManager _manager;
    private readonly ILogger<SearchActionFilter> _log;

    public SearchActionFilter(
        IDtoService dtoService,
        JfresolveManager manager,
        ILogger<SearchActionFilter> log
    )
    {
        _dtoService = dtoService;
        _manager = manager;
        _log = log;
    }

    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // Check if search is enabled in configuration
        if (!JfresolvePlugin.Instance?.Configuration.EnableSearch ?? true)
        {
            await next();
            return;
        }

        // Check if this is a search action and get search term
        if (!IsSearchAction(ctx) || !TryGetSearchTerm(ctx, out var searchTerm))
        {
            await next();
            return;
        }

        // Sanitize search term to prevent injection attacks
        searchTerm = SanitizeSearchTerm(searchTerm);
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            await next();
            return;
        }

        // Handle "local:" prefix - pass through to default Jellyfin search
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm.Substring(6).Trim();
            await next();
            return;
        }

        var config = JfresolvePlugin.Instance?.Configuration;
        bool musicOnly = false;
        if (config?.EnableMusicMode == true && !string.IsNullOrWhiteSpace(config.MusicSearchPrefix)
            && searchTerm.StartsWith(config.MusicSearchPrefix, StringComparison.OrdinalIgnoreCase))
        {
            searchTerm = searchTerm.Substring(config.MusicSearchPrefix.Length).Trim();
            musicOnly = true;
        }

        // Get requested item types from query parameters
        var requestedTypes = GetRequestedItemTypes(ctx, musicOnly);
        if (requestedTypes.Count == 0)
        {
            // No supported types requested, let Jellyfin handle it
            await next();
            return;
        }

        // Get pagination parameters
        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        // Search TMDB and/or Spotify depending on requested types
        var baseItems = await SearchTmdbAndMusicAsync(searchTerm, requestedTypes);

        _log.LogInformation(
            "Jfresolve: Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} results={Results}",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            baseItems.Count
        );

        // Convert BaseItems to DTOs (similar to Gelato's ConvertMetasToDtos)
        var dtos = ConvertBaseItemsToDtos(baseItems);

        // Apply pagination with ordering to avoid EF Core warnings
        // Order by name for consistent pagination results
        var paged = dtos.OrderBy(d => d.Name).Skip(start).Take(limit).ToArray();

        // Return search results
        ctx.Result = new OkObjectResult(
            new QueryResult<BaseItemDto>
            {
                Items = paged,
                TotalRecordCount = dtos.Count
            }
        );
    }

    /// <summary>
    /// Search TMDB for video types and Spotify for Audio (when music mode enabled).
    /// </summary>
    private async Task<List<BaseItem>> SearchTmdbAndMusicAsync(string searchTerm, HashSet<BaseItemKind> requestedTypes)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        var videoTypes = requestedTypes.Where(t => t == BaseItemKind.Movie || t == BaseItemKind.Series).ToHashSet();
        var wantMusic = requestedTypes.Contains(BaseItemKind.Audio) && config?.EnableMusicMode == true;

        var tasks = new List<Task<List<BaseItem>>>();

        foreach (var itemType in videoTypes)
        {
            tasks.Add(_manager.SearchTmdbAsync(searchTerm, itemType));
        }

        if (wantMusic)
        {
            var limit = config?.SearchResultLimit ?? Constants.SpotifyMaxSearchResults;
            tasks.Add(_manager.SearchSpotifyAsync(searchTerm, Math.Min(limit, Constants.SpotifyMaxSearchResults)));
        }

        if (tasks.Count == 0)
            return new List<BaseItem>();

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>
    /// Convert BaseItems to DTOs (based on Gelato's ConvertMetasToDtos)
    /// </summary>
    private List<BaseItemDto> ConvertBaseItemsToDtos(List<BaseItem> baseItems)
    {
        var options = new DtoOptions
        {
            EnableImages = true,
            EnableUserData = false,
        };

        var dtos = new List<BaseItemDto>(baseItems.Count);

        foreach (var baseItem in baseItems)
        {
            var dto = _dtoService.GetBaseItemDto(baseItem, options);

            // Use the BaseItem's ID (already set in JfresolveManager)
            dto.Id = baseItem.Id;

            dtos.Add(dto);
        }

        return dtos;
    }

    private bool IsSearchAction(ActionExecutingContext ctx)
    {
        var actionName = ctx.ActionDescriptor?.DisplayName ?? "";
        return actionName.Contains("GetItems", StringComparison.OrdinalIgnoreCase) ||
               actionName.Contains("GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetSearchTerm(ActionExecutingContext ctx, out string searchTerm)
    {
        searchTerm = string.Empty;

        if (ctx.ActionArguments.TryGetValue("searchTerm", out var value) && value is string term)
        {
            searchTerm = term;
            return !string.IsNullOrWhiteSpace(searchTerm);
        }

        return false;
    }

    private HashSet<BaseItemKind> GetRequestedItemTypes(ActionExecutingContext ctx, bool musicOnly)
    {
        var supportedVideo = new[] { BaseItemKind.Movie, BaseItemKind.Series };
        var supportedMusic = new[] { BaseItemKind.Audio };
        var config = JfresolvePlugin.Instance?.Configuration;
        var allowMusic = config?.EnableMusicMode == true;

        if (musicOnly && allowMusic)
        {
            return new HashSet<BaseItemKind>(supportedMusic);
        }

        var requested = new HashSet<BaseItemKind>(supportedVideo);
        if (allowMusic)
        {
            requested.Add(BaseItemKind.Audio);
        }

        // Check for includeItemTypes parameter
        if (ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes != null
            && includeTypes.Length > 0)
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            requested.IntersectWith(supportedVideo.Concat(allowMusic ? supportedMusic : Array.Empty<BaseItemKind>()));

            // Main-page search: client often sends only [Movie, Series]. Add Audio when music mode is on
            // so that search results show a "Music" category alongside Movies and Shows.
            if (allowMusic && (requested.Contains(BaseItemKind.Movie) || requested.Contains(BaseItemKind.Series)))
            {
                requested.Add(BaseItemKind.Audio);
            }
        }

        // Remove excluded types
        if (ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes != null
            && excludeTypes.Length > 0)
        {
            requested.ExceptWith(excludeTypes);
        }

        // If mediaTypes=Video, exclude Series (Gelato pattern)
        if (ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
            && mediaTypes != null
            && mediaTypes.Contains(MediaType.Video))
        {
            requested.Remove(BaseItemKind.Series);
        }

        return requested;
    }

    /// <summary>
    /// Sanitizes search term to prevent injection attacks
    /// Allows letters, numbers, spaces, and common punctuation for search queries
    /// </summary>
    private static string SanitizeSearchTerm(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Remove control characters
        var sanitized = new string(input.Where(c => !char.IsControl(c)).ToArray()).Trim();
        
        // Allow letters, numbers, spaces, hyphens, apostrophes, and common punctuation
        // This is more permissive than URL sanitization since search terms need more flexibility
        var allowedChars = new HashSet<char>("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -'.,!?()[]");
        sanitized = new string(sanitized.Where(c => allowedChars.Contains(c)).ToArray());
        
        // Limit length to prevent buffer overflow attacks
        const int maxLength = 200;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength);
        }
        
        return sanitized;
    }
}

// Helper extension methods
public static class ActionContextExtensions
{
    public static bool TryGetActionArgument<T>(
        this ActionExecutingContext ctx,
        string key,
        out T value,
        T defaultValue = default!)
    {
        if (ctx.ActionArguments.TryGetValue(key, out var objValue) && objValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = defaultValue!;
        return false;
    }
}
