namespace SubathonManager.Core.Interfaces;

public interface IWebhookIntegration : IAppService
{
    string WebhookPath { get; }

    Task HandleWebhookAsync(byte[] rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
}
