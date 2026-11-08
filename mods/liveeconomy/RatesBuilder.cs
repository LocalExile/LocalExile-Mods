using System.Text.Json;

namespace LiveEconomy;

/// <summary>An item's value in chaos, how much chaos of it traded, and its value through the hours when it changed: [hour number, chaos], 0 for no price then.</summary>
sealed record Price(double Chaos, double Volume, double[][]? History = null);

/// <summary>The file LocalExile's Currency Exchange reads.</summary>
sealed record Rates(long SourceHour, string Current, Dictionary<string, Dictionary<string, Price>> Leagues, int Hours);

/// <summary>Prices from GGG's hourly Currency Exchange digests. Every price is in chaos, so that no path through several items pays more than it costs.</summary>
static class RatesBuilder
{
    const string Chaos = "Metadata/Items/Currency/CurrencyRerollRare", Divine = "Metadata/Items/Currency/CurrencyModValues", Standard = "Standard";

    /// <summary>An item below this much chaos traded is too thin to price.</summary>
    const double MinChaosVolume = 5;

    /// <summary>How many of the newest hours make an item's price when it traded enough in them.</summary>
    const int RecentHours = 24;

    /// <summary>What share of its volume over all the digests an item needs in those hours: less is a stray trade, not the market moving.</summary>
    const double MinRecentShare = 0.05;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>One digest's trades of an item for chaos or divines.</summary>
    readonly record struct Trade(double Count, double Paid, bool InDivines, long Hour);

    /// <summary>The file from the digests, <b>newest first</b>. An item traded enough in the last <see cref="RecentHours"/> has that price, which follows the
    /// market; a rarer one has its price over all the digests.</summary>
    public static string Build(IEnumerable<string> digests)
    {
        var trades = new Dictionary<string, Dictionary<string, List<Trade>>>();
        long newest = 0;
        var hours = new SortedSet<long>();
        foreach (string digest in digests)
        {
            using var document = JsonDocument.Parse(digest);
            var root = document.RootElement;
            long hour = root.GetProperty("next_change_id").GetInt64() - 3600;
            newest = Math.Max(newest, hour);
            hours.Add(hour);
            foreach (var market in root.GetProperty("markets").EnumerateArray())
            {
                string? league = market.GetProperty("league").GetString();
                if (league is null || league.Contains("(PL", StringComparison.Ordinal)) continue;
                var pair = market.GetProperty("market_pair");
                string a = pair[0].GetString()!, b = pair[1].GetString()!;
                var traded = market.GetProperty("volume_traded");
                if (!traded.TryGetProperty(a, out var va) || !traded.TryGetProperty(b, out var vb) || va.GetDouble() <= 0 || vb.GetDouble() <= 0) continue;
                Add(a, b, va.GetDouble(), vb.GetDouble());
                Add(b, a, vb.GetDouble(), va.GetDouble());

                void Add(string item, string hub, double count, double paid)
                {
                    if (hub is not (Chaos or Divine)) return;
                    if (!trades.TryGetValue(league, out var items)) trades[league] = items = [];
                    if (!items.TryGetValue(item, out var list)) items[item] = list = [];
                    list.Add(new(count, paid, hub == Divine, hour));
                }
            }
        }
        var leagues = trades.ToDictionary(l => l.Key, l =>
        {
            var prices = PricesOver(l.Value, long.MinValue, newest);
            foreach (var (item, price) in PricesOver(l.Value, newest - RecentHours * 3600, newest))
                if (prices.TryGetValue(item, out var week) && price.Volume >= week.Volume * MinRecentShare) prices[item] = week with { Chaos = price.Chaos };
            return prices;
        });
        string current = leagues.Where(l => l.Key != Standard && !l.Key.StartsWith("Hardcore", StringComparison.Ordinal))
            .OrderByDescending(l => l.Value.Count).Select(l => l.Key).FirstOrDefault() ?? Standard;
        foreach (string league in new[] { Standard, current }.Distinct())
            if (trades.TryGetValue(league, out var items)) AddHistory(leagues[league], items, hours);
        return JsonSerializer.Serialize(new Rates(newest, current, leagues, hours.Count), Options);
    }

    /// <summary>The current challenge league in a file of rates.</summary>
    public static string? CurrentOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("current", out var current) ? current.GetString() : null;
    }

    /// <summary>What an order that waited through those hours would have seen: each hour's price by the same rule as the current one. Only the leagues a
    /// server trades at have it, and only its changes, to 4 digits, so the file stays small.</summary>
    static void AddHistory(Dictionary<string, Price> prices, Dictionary<string, List<Trade>> items, SortedSet<long> hours)
    {
        var history = prices.Keys.ToDictionary(item => item, _ => new List<double[]>());
        foreach (long hour in hours)
        {
            var then = PricesOver(items, hour - RecentHours * 3600, hour);
            foreach (var (item, changes) in history)
            {
                double value = then.TryGetValue(item, out var price) && price.Volume >= prices[item].Volume * MinRecentShare ? Round(price.Chaos) : 0;
                if (changes.Count == 0 ? value != 0 : changes[^1][1] != value) changes.Add([hour / 3600, value]);
            }
        }
        foreach (var (item, changes) in history)
            if (item != Chaos && changes.Count > 0) prices[item] = prices[item] with { History = [.. changes] };
    }

    static double Round(double value)
    {
        if (value <= 0) return 0;
        double scale = Math.Pow(10, 3 - Math.Floor(Math.Log10(value)));
        return Math.Round(value * scale) / scale;
    }

    /// <summary>Prices from the trades after one hour, up to and including another.</summary>
    static Dictionary<string, Price> PricesOver(Dictionary<string, List<Trade>> items, long after, long upTo)
    {
        var prices = new Dictionary<string, Price>();
        bool Counts(Trade t) => t.Hour > after && t.Hour <= upTo;
        double divine = items.TryGetValue(Divine, out var divines) && Median([.. divines.Where(t => Counts(t) && !t.InDivines).Select(t => (t.Paid / t.Count, t.Count))]) is { } value ? value : 0;
        double chaosVolume = 0;
        foreach (var (item, list) in items)
        {
            if (item == Chaos) continue;
            // In chaos, each hour's trades at their own price. A divine pair counts with the chaos pair: dear items trade for divines, and
            // their few chaos trades can be far off (The Doctor, in a day: 9 at 231 chaos, 96 at 665 in divines).
            var priced = list.Where(t => Counts(t) && (!t.InDivines || divine > 0)).Select(t => (Chaos: t.InDivines ? t.Paid * divine : t.Paid, t.Count)).ToList();
            double volume = priced.Sum(t => t.Chaos);
            chaosVolume += list.Where(t => Counts(t) && !t.InDivines).Sum(t => t.Paid);
            if (volume >= MinChaosVolume && Median([.. priced.Select(t => (t.Chaos / t.Count, t.Count))]) is { } price) prices[item] = new(price, volume);
        }
        if (prices.Count > 0) prices[Chaos] = new(1, chaosVolume);
        return prices;
    }

    /// <summary>The price of the middle item traded. An average by chaos follows the dear trades, which are what a mistake or a scam looks like
    /// (The Forward Gaze, in a week: 4 at 1 chaos beside 4 at about 133), and the median by items does not.</summary>
    static double? Median(IReadOnlyList<(double Price, double Count)> trades)
    {
        double half = trades.Sum(t => t.Count) / 2, passed = 0;
        foreach (var (price, count) in trades.OrderBy(t => t.Price))
            if ((passed += count) >= half) return price;
        return null;
    }
}
