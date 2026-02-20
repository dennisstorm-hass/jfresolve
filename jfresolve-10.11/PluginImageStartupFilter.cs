using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jfresolve;

/// <summary>
/// Serves the plugin image via middleware so the Image route does not depend on controller DI.
/// Jellyfin activates plugin controllers from a scope that may not have ILoggerFactory.
/// </summary>
public class PluginImageStartupFilter : IStartupFilter
{
    private static readonly string ImagePathPrefix = "/Plugins/506f18b85dad4cd3b9a0f7ed933e9939";
    private static readonly string[] ResourceNames = { "Jfresolve.jfresolve.png", "jfresolve.jfresolve.png", "Jfresolve.jfresolve-10.11.jfresolve.png" };

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(ServePluginImageMiddleware);
            next(app);
        };
    }

    private static async Task ServePluginImageMiddleware(HttpContext context, Func<Task> next)
    {
        if (!string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith(ImagePathPrefix, StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith("/Image", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();
        Stream? imageStream = null;
        foreach (var name in ResourceNames)
        {
            imageStream = assembly.GetManifestResourceStream(name);
            if (imageStream != null) break;
        }

        if (imageStream == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        try
        {
            context.Response.ContentType = "image/png";
            context.Response.StatusCode = 200;
            await imageStream.CopyToAsync(context.Response.Body);
        }
        finally
        {
            await imageStream.DisposeAsync();
        }
    }
}
