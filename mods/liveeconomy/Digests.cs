using System.Net;

namespace LiveEconomy;

/// <summary>GGG's public hourly Currency Exchange digests, kept in a folder: each is fixed once its hour has passed, so it is asked for once.</summary>
static class Digests
{
    const string Address = "https://web.poecdn.com/api/currency-exchange/";

    public static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalExile-rates/1.0");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("(+https://github.com/LocalExile/LocalExile)");
        return http;
    }

    /// <summary>Brings the folder up to the last <paramref name="hours"/>, newest asked for first, and builds the rates from what it holds; null when it holds
    /// nothing. GGG may remove old history: an hour it refuses is skipped, and the rest still count.</summary>
    public static async Task<string?> Update(HttpClient http, string folder, int hours, CancellationToken stop)
    {
        Directory.CreateDirectory(folder);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600, newest = now - 3600, oldest = now - hours * 3600;
        for (long hour = newest; hour >= oldest; hour -= 3600)
        {
            string path = Path.Combine(folder, $"digest-{hour}.json");
            if (File.Exists(path)) continue;
            using var response = await http.GetAsync(Address + hour, stop);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1), stop);
                break;
            }
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[rates] hour {hour}: {(int)response.StatusCode}, skipped");
                continue;
            }
            string digest = await response.Content.ReadAsStringAsync(stop);
            if (digest.Contains($"\"next_change_id\":{hour + 3600}", StringComparison.Ordinal)) // else not published yet
                await File.WriteAllTextAsync(path, digest, stop);
            await Task.Delay(TimeSpan.FromSeconds(1), stop);
        }
        foreach (string file in Directory.GetFiles(folder, "digest-*.json"))
            if (long.TryParse(Path.GetFileNameWithoutExtension(file)["digest-".Length..], out long hour) && hour < oldest) File.Delete(file);
        string[] held = [.. Directory.GetFiles(folder, "digest-*.json").OrderDescending(StringComparer.Ordinal)];
        return held.Length == 0 ? null : RatesBuilder.Build(held.Select(File.ReadAllText));
    }

    /// <summary>How long until the next digest is published: it comes when its hour ends.</summary>
    public static TimeSpan UntilNext() => TimeSpan.FromSeconds(3600 - DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 3600 + 300);
}
