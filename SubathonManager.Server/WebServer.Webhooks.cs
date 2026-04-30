using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Server;

public partial class WebServer
{
    private void SetupWebhookRoutes()
    {
        foreach (var integration in AppServices.Provider.GetServices<IWebhookIntegration>())
            RegisterWebhookRoute(integration);
    }

    private void RegisterWebhookRoute(IWebhookIntegration integration)
    {
        var captured = integration;
        _routes.Add((new RouteKey("POST", captured.WebhookPath), async ctx =>
        {
            var headers = ctx.Headers;
            using var ms = new MemoryStream();
            await ctx.Body.CopyToAsync(ms);
            byte[] rawBody = ms.ToArray();
            await ctx.WriteResponse(200, "OK");
            await captured.HandleWebhookAsync(rawBody, headers);
        }));
        _logger?.LogDebug("Registered webhook route: POST {Path}", captured.WebhookPath);
    }
}
