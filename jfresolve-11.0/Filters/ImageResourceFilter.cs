using System;
using System.Threading.Tasks;
using Jfresolve.Provider;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters
{
    public class ImageResourceFilter : IAsyncResourceFilter
    {
        private readonly ILogger<ImageResourceFilter> _logger;
        private readonly JfresolveProvider _provider;

        public ImageResourceFilter(ILogger<ImageResourceFilter> logger, JfresolveProvider provider)
        {
            _logger = logger;
            _provider = provider;
        }

        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            if (context.ActionDescriptor is not ControllerActionDescriptor cad
                || cad.ActionName != "GetItemImage")
            {
                await next();
                return;
            }

            if (!context.RouteData.Values.TryGetValue("itemId", out var idObject) || !Guid.TryParse(idObject?.ToString(), out var id))
            {
                await next();
                return;
            }

            if (_provider.MetaCache.TryGetValue(id, out var meta))
            {
                _logger.LogInformation("ImageResourceFilter is intercepting image request for item {Id}", id);

                // Determine which image type is being requested
                var imageType = context.RouteData.Values.TryGetValue("imageType", out var typeObj)
                    ? typeObj?.ToString()
                    : null;

                // Try to serve the appropriate image based on type
                string? imageUrl = null;

                if (imageType == "Backdrop" && !string.IsNullOrEmpty(meta.Backdrop))
                {
                    imageUrl = meta.Backdrop;
                }
                else if (!string.IsNullOrEmpty(meta.Poster))
                {
                    // Default to poster for any other type (Primary, Thumb, etc.)
                    imageUrl = meta.Poster;
                }

                if (!string.IsNullOrEmpty(imageUrl))
                {
                    context.Result = new RedirectResult(imageUrl, permanent: false);
                    return;
                }
            }

            await next();
        }
    }
}
