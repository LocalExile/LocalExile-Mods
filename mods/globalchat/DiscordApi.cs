using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GlobalChat;

sealed class DiscordApiException(int status) : Exception($"Discord HTTP {status}")
{
    internal int Status => status;
}

/// <summary>One worker uses the client serially, sharing Discord's rate-limit cooldown across both directions.</summary>
sealed class DiscordApi
{
    readonly HttpClient http;
    readonly Func<TimeSpan, CancellationToken, Task> wait;
    readonly TimeProvider time;
    DateTimeOffset nextRequest;

    internal DiscordApi(HttpClient http, string token, Func<TimeSpan, CancellationToken, Task>? wait = null, TimeProvider? time = null)
    {
        this.http = http;
        this.wait = wait ?? ((delay, cancel) => Task.Delay(delay, cancel));
        this.time = time ?? TimeProvider.System;
        http.BaseAddress ??= new Uri("https://discord.com/api/v10/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/LocalExile/LocalExile-Mods, 1.2.16)");
    }

    internal async Task<JsonElement> Request(HttpMethod method, string path, string? body, CancellationToken cancel)
    {
        for (int attempt = 0; ; attempt++)
        {
            var cooldown = nextRequest - time.GetUtcNow();
            if (cooldown > TimeSpan.Zero) await wait(cooldown, cancel).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            string content = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                using var limited = JsonDocument.Parse(content);
                double seconds = limited.RootElement.GetProperty("retry_after").GetDouble();
                if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86400) throw new InvalidDataException("Invalid Discord cooldown.");
                nextRequest = time.GetUtcNow().AddSeconds(Math.Max(.1, seconds));
                if (attempt < 3) continue;
            }
            if ((int)response.StatusCode >= 500 && attempt < 3)
            {
                nextRequest = time.GetUtcNow().AddSeconds(1 << attempt);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new DiscordApiException((int)response.StatusCode);
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"
                && response.Headers.TryGetValues("X-RateLimit-Reset-After", out var reset)
                && double.TryParse(reset.FirstOrDefault(), CultureInfo.InvariantCulture, out double delay)
                && double.IsFinite(delay) && delay is >= 0 and <= 86400)
                nextRequest = time.GetUtcNow().AddSeconds(delay);
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
    }
}
