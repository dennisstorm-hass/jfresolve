using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jfresolve.Utilities
{
    /// <summary>
    /// Utility class for extracting parameters from HTTP routes and query strings.
    /// Consolidates parameter extraction logic that was duplicated across filters.
    /// Extracted from: PlaybackInfoFilter.cs, InsertActionFilter.cs, ImageResourceFilter.cs
    /// </summary>
    public static class RouteParameterExtractor
    {
        /// <summary>
        /// Tries to extract a GUID parameter from route data or action arguments.
        /// Attempts multiple possible parameter names for compatibility.
        /// </summary>
        public static bool TryGetItemId(ActionExecutingContext ctx, out Guid itemId)
        {
            itemId = Guid.Empty;

            if (ctx == null)
                return false;

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

        /// <summary>
        /// Tries to extract a GUID parameter from route data, action arguments, or query string.
        /// This is a more comprehensive version that also checks query string as fallback.
        /// </summary>
        public static bool TryGetItemIdWithQueryFallback(ActionExecutingContext ctx, out Guid itemId)
        {
            // Try standard extraction first
            if (TryGetItemId(ctx, out itemId))
                return true;

            // Fallback: check query string "ids"
            if (ctx?.HttpContext?.Request?.Query != null)
            {
                var query = ctx.HttpContext.Request.Query;
                if (query.TryGetValue("ids", out var ids) && ids.Count == 1)
                {
                    var s = ids[0];
                    if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out var g))
                    {
                        itemId = g;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Tries to extract a string parameter from route data.
        /// </summary>
        public static bool TryGetStringParameter(ActionExecutingContext ctx, string parameterName, out string? value)
        {
            value = null;

            if (ctx == null || string.IsNullOrWhiteSpace(parameterName))
                return false;

            var rd = ctx.RouteData.Values;
            if (rd.TryGetValue(parameterName, out var raw) && raw is not null)
            {
                value = raw.ToString();
                return !string.IsNullOrWhiteSpace(value);
            }

            return false;
        }

        /// <summary>
        /// Tries to extract a GUID parameter from a specific route parameter name.
        /// </summary>
        public static bool TryGetGuidParameter(ActionExecutingContext ctx, string parameterName, out Guid value)
        {
            value = Guid.Empty;

            if (ctx == null || string.IsNullOrWhiteSpace(parameterName))
                return false;

            var rd = ctx.RouteData.Values;
            if (rd.TryGetValue(parameterName, out var raw) && raw is not null)
            {
                var s = raw.ToString();
                if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out var g))
                {
                    value = g;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if the current action is of a specific type.
        /// </summary>
        public static bool IsActionName(ActionExecutingContext ctx, string actionName)
        {
            if (ctx?.ActionDescriptor is not ControllerActionDescriptor cad)
                return false;

            return string.Equals(cad.ActionName, actionName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
