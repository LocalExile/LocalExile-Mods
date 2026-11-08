using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Modding;

namespace LiveEconomy;

/// <summary>Names the rates service and how the exchange trades; the Gateway does the rest, and the mod contacts nothing.</summary>
public sealed class LiveEconomyMod : IMod
{
    static readonly string[] PriceSettings = ["standardPrices", "leaguePrices"];

    sealed record Choices(string Url, int OrderSlots, string StandardPrices, string LeaguePrices, double SpreadScale, double VolumeShare, double PatientRange,
        double PatientSpeed, int CatchUpHours, double GoldFee, double MaxRateAgeDays, bool FaustusSpeaks);

    static ExchangeRatesLeague League(string choice) => choice == "standard" ? ExchangeRatesLeague.Standard : ExchangeRatesLeague.Current;

    /// <inheritdoc/>
    public void Load(IModContext mod)
    {
        var c = mod.Settings<Choices>();
        mod.UseExchangeRates(c.Url, new(League(c.StandardPrices), League(c.LeaguePrices), c.OrderSlots, c.SpreadScale, c.VolumeShare, c.PatientRange,
            c.PatientSpeed, c.CatchUpHours, c.GoldFee, c.MaxRateAgeDays, c.FaustusSpeaks), league => ShowLeague(mod, league));
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
