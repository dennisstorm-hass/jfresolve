using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters;

/// <summary>
/// Filter to mark Jfresolve items as deletable in DTOs so the UI shows the delete button
/// Jellyfin's UI checks if items have physical files before showing delete, but our items are virtual
/// </summary>
public sealed class ItemDtoFilter : IAsyncResultFilter, IOrderedFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ItemDtoFilter> _log;
    private readonly JfresolveManager _manager;
    private readonly IUserManager _userManager;

    public ItemDtoFilter(
        ILibraryManager libraryManager,
        JfresolveManager manager,
        IUserManager userManager,
        ILogger<ItemDtoFilter> log
    )
    {
        _libraryManager = libraryManager;
        _log = log;
        _manager = manager;
        _userManager = userManager;
    }

    public int Order => 10; // Run after other filters

    public async Task OnResultExecutionAsync(
        ResultExecutingContext ctx,
        ResultExecutionDelegate next
    )
    {
        await next();

        // Only process successful results that return item DTOs
        if (ctx.Result is not OkObjectResult okResult)
            return;

        // Get the current user from context
        var userId = ctx.HttpContext.User?.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var userGuid))
            return;

        var user = _userManager.GetUserById(userGuid);
        if (user == null)
            return;

        // Handle single item DTO
        if (okResult.Value is BaseItemDto singleDto)
        {
            MarkAsDeletableIfJfresolve(singleDto, user);
            return;
        }

        // Handle QueryResult<BaseItemDto> (list of items)
        if (okResult.Value is QueryResult<BaseItemDto> queryResult && queryResult.Items != null)
        {
            foreach (var dto in queryResult.Items)
            {
                MarkAsDeletableIfJfresolve(dto, user);
            }
            return;
        }

        // Handle array/list of BaseItemDto
        if (okResult.Value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is BaseItemDto dto)
                {
                    MarkAsDeletableIfJfresolve(dto, user);
                }
            }
        }
    }

    private void MarkAsDeletableIfJfresolve(BaseItemDto dto, User user)
    {
        if (dto == null || dto.Id == Guid.Empty)
            return;

        try
        {
            // Get the BaseItem to check if it's a Jfresolve item
            var item = _libraryManager.GetItemById<BaseItem>(dto.Id, user);
            if (item == null || !_manager.IsJfresolve(item))
                return;

            // Check if user can delete it
            if (!_manager.CanDelete(item, user))
                return;

            // Ensure Path is set (required for Jellyfin to recognize the item)
            if (string.IsNullOrWhiteSpace(dto.Path))
            {
                dto.Path = item.Path ?? $"jfresolve://item/{dto.Id}";
            }

            // Try to set deletability-related properties using reflection
            // Jellyfin's UI may check various properties to determine if delete button should be shown
            var dtoType = typeof(BaseItemDto);
            
            // Try to set CanDelete property if it exists
            var canDeleteProp = dtoType.GetProperty("CanDelete", BindingFlags.Public | BindingFlags.Instance);
            if (canDeleteProp != null && canDeleteProp.CanWrite)
            {
                canDeleteProp.SetValue(dto, true);
            }

            // Try to set HasPath property if it exists (some versions of Jellyfin use this)
            var hasPathProp = dtoType.GetProperty("HasPath", BindingFlags.Public | BindingFlags.Instance);
            if (hasPathProp != null && hasPathProp.CanWrite)
            {
                hasPathProp.SetValue(dto, true);
            }

            // Try to set IsVirtualItem property to false if it exists
            var isVirtualProp = dtoType.GetProperty("IsVirtualItem", BindingFlags.Public | BindingFlags.Instance);
            if (isVirtualProp != null && isVirtualProp.CanWrite)
            {
                isVirtualProp.SetValue(dto, false);
            }
            
            _log.LogDebug("Jfresolve: Marked item '{Name}' (ID: {Id}) as deletable in DTO", 
                dto.Name, dto.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Jfresolve: Failed to mark item {Id} as deletable in DTO", dto.Id);
        }
    }
}
