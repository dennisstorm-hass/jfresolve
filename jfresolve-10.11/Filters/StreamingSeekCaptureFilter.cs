using System;
using System.Linq;
using Jfresolve.Services;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jfresolve.Filters;

/// <summary>
/// Records startTimeTicks from Jellyfin video streaming requests before FFmpeg restarts.
/// </summary>
public sealed class StreamingSeekCaptureFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var request = context.HttpContext.Request;

        if (TryReadStartTicks(request.Query, out var queryTicks))
        {
            SeekPositionCache.SetPending(queryTicks);
            return;
        }

        var path = request.Path.Value ?? string.Empty;
        if (!path.Contains("/Videos/", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/Items/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument == null)
                continue;

            var prop = argument.GetType().GetProperty("StartTimeTicks")
                ?? argument.GetType().GetProperty("startTimeTicks");
            if (prop?.PropertyType == typeof(long?) && prop.GetValue(argument) is long ticks && ticks > 0)
            {
                SeekPositionCache.SetPending(ticks);
                return;
            }

            if (prop?.PropertyType == typeof(long) && prop.GetValue(argument) is long ticksNonNull && ticksNonNull > 0)
            {
                SeekPositionCache.SetPending(ticksNonNull);
                return;
            }
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    private static bool TryReadStartTicks(Microsoft.AspNetCore.Http.IQueryCollection query, out long ticks)
    {
        ticks = 0;
        foreach (var key in new[] { "startTimeTicks", "StartTimeTicks" })
        {
            if (query.TryGetValue(key, out var values)
                && long.TryParse(values.FirstOrDefault(), out ticks)
                && ticks > 0)
            {
                return true;
            }
        }

        return false;
    }
}
