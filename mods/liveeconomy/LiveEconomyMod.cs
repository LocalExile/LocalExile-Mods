using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Modding;

namespace LiveEconomy;

/// <summary>Gets the rates, from the server in its settings or, with none, from GGG itself, and hands them to the Currency Exchange.</summary>
public sealed class LiveEconomyMod : IMod, IDisposable
{
    /// <summary>Beside the Gateway's data, not in the mod's folder: a change there would reload the mod.</summary>
    const string DigestFolder = "data/liveeconomy";

    static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(5);
    static readonly string[] PriceSettings = ["standardPrices", "leaguePrices"];

    readonly CancellationTokenSource _stop = new();

    sealed record Choices(int OrderSlots, string StandardPrices, string LeaguePrices, double SpreadScale, double VolumeShare, double PatientRange, double PatientSpeed,
        int CatchUpHours, double GoldFee, double MaxRateAgeDays, bool FaustusSpeaks, string Host, int Port, int DirectHours);

    static ExchangeRatesLeague League(string choice) => choice == "standard" ? ExchangeRatesLeague.Standard : ExchangeRatesLeague.Current;

    /// <inheritdoc/>
    public void Load(IModContext mod)
    {
        var c = mod.Settings<Choices>();
        mod.UseExchangeOptions(new(League(c.StandardPrices), League(c.LeaguePrices), c.OrderSlots, c.SpreadScale, c.VolumeShare, c.PatientRange,
            c.PatientSpeed, c.CatchUpHours, c.GoldFee, c.MaxRateAgeDays, c.FaustusSpeaks));
        string host = c.Host.Trim();
        var token = _stop.Token;
        _ = Task.Run(() => KeepUpdated(mod, host.Length > 0 && c.Port > 0 ? new Uri($"http://{host}:{c.Port}/rates.json") : null, c.DirectHours, token), token);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    static async Task KeepUpdated(IModContext mod, Uri? server, int directHours, CancellationToken stop)
    {
        using var http = Digests.NewClient();
        EntityTagHeaderValue? seen = null;
        mod.Log(server is null ? "no server in the settings: getting the rates from GGG" : $"getting the rates from {server}");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    string? json;
                    if (server is null)
                    {
                        json = await Digests.Update(http, DigestFolder, directHours, stop);
                        wait = Digests.UntilNext();
                    }
                    else
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, server);
                        if (seen is not null) request.Headers.IfNoneMatch.Add(seen);
                        using var response = await http.SendAsync(request, stop);
                        json = null;
                        if (response.StatusCode != HttpStatusCode.NotModified)
                        {
                            response.EnsureSuccessStatusCode();
                            json = await response.Content.ReadAsStringAsync(stop);
                            seen = response.Headers.ETag;
                        }
                        wait = TimeSpan.FromHours(1);
                    }
                    if (json is not null && !stop.IsCancellationRequested)
                    {
                        mod.PublishExchangeRates(json);
                        if (RatesBuilder.CurrentOf(json) is { } league) ShowLeague(mod, league);
                    }
                }
                catch (Exception e) when (!stop.IsCancellationRequested && e is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or FormatException
                    or KeyNotFoundException or InvalidOperationException or OperationCanceledException) // cancelled and not stopping: a timeout
                {
                    mod.Log($"{e.Message} (the last rates stay)");
                    wait = AfterFailure;
                }
                await Task.Delay(wait, stop);
            }
        }
        catch (Exception e) when (stop.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>Puts the current league's name in the setting's own label, for an editor to show. Written only when it changes, as the write reloads the mod.</summary>
    static void ShowLeague(IModContext mod, string league)
    {
        string path = Path.Combine(mod.Folder, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(path))!;
        string label = $"Current league ({league})";
        bool changed = false;
        foreach (string name in PriceSettings)
            foreach (var option in settings["settings"]![name]!["options"]!.AsArray())
                if ((string?)option!["value"] == "current" && (string?)option["label"] != label)
                {
                    option["label"] = label;
                    changed = true;
                }
        if (changed) File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
