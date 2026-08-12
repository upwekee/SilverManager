using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SteamVault.Services;

public sealed class WebhookService
{
    public async Task NotifyAsync(string? webhookUrl, string title, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // Discord-compatible simple payload
            var payload = JsonSerializer.Serialize(new
            {
                content = $"**{title}**\n{message}"
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await http.PostAsync(webhookUrl, content, ct);
        }
        catch
        {
            // fire-and-forget
        }
    }
}
