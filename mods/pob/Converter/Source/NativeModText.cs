using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Current client descriptions bind every displayed number to its native stat, including conditional wording.
static class NativeModText
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    internal static readonly Regex Number = new(@"([+-]?)\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)|[+-]?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
    static readonly Regex Placeholder = new(@"\{(\d*)(?::([^}]+))?\}", RegexOptions.CultureInvariant);
    public sealed record Binding(int Stat, double Scale, double Literal = 0, int? Precision = null);
    public sealed record Condition(int Stat, string Text);
    public sealed record Pattern(string Id, string[] Lines, int[] Defaults, Binding[] Bindings, Condition[] Conditions)
    {
        public int[]? Values(double[] numbers)
        {
            if (numbers.Length != Bindings.Length) return null;
            var values = Defaults.ToArray();
            var assigned = new Dictionary<int, int>();
            for (int n = 0; n < numbers.Length; n++)
            {
                var b = Bindings[n];
                if (b.Stat < 0) { if (Math.Abs(numbers[n] - b.Literal) > 1e-6) return null; continue; }
                double native = numbers[n] / b.Scale;
                if (!double.IsFinite(native) || native < int.MinValue || native > int.MaxValue) return null;
                if (Math.Abs(native - Math.Round(native)) > 1e-5)
                {
                    if (b.Precision is not int precision) return null;
                    double chosen = Math.Round(native), tolerance = Math.Pow(10, -precision) / 2 + 1e-9;
                    bool Displays(double value) => Math.Abs(value * b.Scale - numbers[n]) < tolerance;
                    // Accept only a unique native integer preimage of the rounded display, never an approximation.
                    if (!Displays(chosen) || Displays(chosen - 1) || Displays(chosen + 1)) return null;
                    native = chosen;
                }
                int value = checked((int)Math.Round(native));
                if (assigned.TryGetValue(b.Stat, out int previous) && previous != value) return null;
                assigned[b.Stat] = values[b.Stat] = value;
            }
            return Conditions.All(c => Holds(c.Text, c.Stat < 0 ? 0 : values[c.Stat])) ? values : null;
        }
    }
    sealed record Fragment(string Text, Binding[] Bindings);
    sealed record State(int[] Low, int[] High, HashSet<string> Done, List<Fragment> Fragments, List<Condition> Conditions);

    public static IReadOnlyList<Pattern> Build()
    {
        var descriptions = StatDescriptions.Load();
        if (descriptions.All.Count == 0) throw new InvalidDataException("Missing stat_descriptions in client.db; extract current client data.");
        var byStat = descriptions.All.SelectMany(d => d.Stats.Select(s => (Stat: s, Description: d))).ToLookup(p => p.Stat, p => p.Description);
        var result = new List<Pattern>();
        foreach (var mod in new ModCatalog("data").All)
        {
            var positions = mod.Stats.Select((s, i) => (s.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Last().i);
            var states = new List<State> { new(mod.Stats.Select(s => Math.Min(s.Min, s.Max)).ToArray(), mod.Stats.Select(s => Math.Max(s.Min, s.Max)).ToArray(), [], [], []) };
            foreach (var stat in mod.Stats)
            {
                var next = new List<State>();
                foreach (var state in states)
                {
                    if (state.Done.Contains(stat.Id)) { next.Add(state); continue; }
                    int at = positions[stat.Id];
                    if (stat.Id is "local_jewel_effect_base_radius" or "dummy_stat_display_nothing" or "number_of_stackable_unique_jewels"
                        && state.Low[at] == state.High[at])
                    {
                        next.Add(state with { Done = new HashSet<string>(state.Done) { stat.Id } });
                        continue;
                    }
                    // Radius is an item property, not a modifier line. Zero stats remain in the mod,
                    // but are hidden only if the client has no matching display variant for them.
                    bool hidden = state.Low[at] == state.High[at] && (stat.Id == "local_jewel_effect_base_radius" || state.Low[at] == 0);
                    bool rendered = false;
                    foreach (var description in byStat[stat.Id])
                    {
                        foreach (var variant in description.Variants)
                        {
                            int[] low = state.Low.ToArray(), high = state.High.ToArray();
                            var conditions = state.Conditions.ToList();
                            bool valid = true;
                            for (int i = 0; i < description.Stats.Count; i++)
                            {
                                int position = positions.GetValueOrDefault(description.Stats[i], -1);
                                string condition = variant.Conditions[i];
                                if (position < 0) valid &= Holds(condition, 0);
                                else valid &= Restrict(condition, ref low[position], ref high[position]);
                                conditions.Add(new(position, condition));
                            }
                            if (!valid || Render(variant, description.Stats, positions, low, high) is not { } fragment) continue;
                            var done = new HashSet<string>(state.Done);
                            done.UnionWith(description.Stats);
                            next.Add(new(low, high, done, [.. state.Fragments, fragment], conditions));
                            rendered = true;
                        }
                        if (rendered) break;
                    }
                    if (!rendered && hidden) next.Add(state with { Done = new HashSet<string>(state.Done) { stat.Id } });
                }
                states = next;
                if (states.Count == 0) break;
            }
            foreach (var state in states)
            {
                if (state.Fragments.Count == 0) continue;
                var bindings = state.Fragments.SelectMany(f => f.Bindings).ToArray();
                // Never guess an unprinted variable. Conditional wording can select an exact value
                // (e.g. Rakiata selects the Timeless Jewel's keystone index 2).
                if (Enumerable.Range(0, mod.Stats.Count).Any(i => state.Low[i] != state.High[i] && !bindings.Any(b => b.Stat == i))) continue;
                result.Add(new(mod.Id, state.Fragments.SelectMany(f => f.Text.Replace("\\n", "\n").Split('\n')).ToArray(), state.Low, bindings, state.Conditions.ToArray()));
            }
        }
        return result;
    }

    static Fragment? Render(StatDescriptions.Variant variant, IReadOnlyList<string> stats, Dictionary<string, int> positions, int[] low, int[] high)
    {
        var scales = Enumerable.Repeat(1d, stats.Count).ToArray();
        var precision = new int?[stats.Count];
        foreach (var (name, index) in variant.Handlers)
        {
            if (name == "canonical_stat") continue; // Description metadata, not an arithmetic transform.
            if (index < 1 || index > scales.Length) return null;
            double? factor = name switch
            {
                "negate" => -1, "divide_by_one_hundred" or "divide_by_one_hundred_2dp_if_required" => .01,
                "per_minute_to_per_second" or "per_minute_to_per_second_2dp" or "per_minute_to_per_second_0dp" or "per_minute_to_per_second_1dp" or "per_minute_to_per_second_2dp_if_required" => 1d / 60,
                "milliseconds_to_seconds" or "milliseconds_to_seconds_2dp" or "milliseconds_to_seconds_2dp_if_required" => .001,
                "divide_by_ten" or "divide_by_ten_0dp" or "deciseconds_to_seconds" or "locations_to_metres" => .1,
                "divide_by_two_0dp" => .5, "divide_by_five" => .2, "divide_by_one_thousand" => .001,
                "double" => 2, "times_twenty" => 20, _ => null,
            };
            if (factor is null) return null;
            scales[index - 1] *= factor.Value;
            if (name.Contains("2dp", StringComparison.Ordinal)) precision[index - 1] = 2;
            else if (name.Contains("1dp", StringComparison.Ordinal)) precision[index - 1] = 1;
            else if (name.Contains("0dp", StringComparison.Ordinal)) precision[index - 1] = 0;
            else if (name == "per_minute_to_per_second") precision[index - 1] = 2;
        }
        var output = new StringBuilder();
        var bindings = new List<Binding>();
        void Literal(string text)
        {
            output.Append(text);
            foreach (Match number in Number.Matches(text)) bindings.Add(new(-1, 1, double.Parse(number.Value, NumberStyles.Float, Invariant)));
        }
        int end = 0, automatic = 0;
        foreach (Match placeholder in Placeholder.Matches(variant.Text))
        {
            Literal(variant.Text[end..placeholder.Index]);
            int index = placeholder.Groups[1].Length == 0 ? automatic++ : int.Parse(placeholder.Groups[1].Value, Invariant);
            if (index >= stats.Count) return null;
            int position = positions.GetValueOrDefault(stats[index], -1);
            double lo = position < 0 ? 0 : low[position] * scales[index], hi = position < 0 ? 0 : high[position] * scales[index];
            string Num(double n) => n.ToString("0.########", Invariant);
            string sign = placeholder.Groups[2].Value.Contains('+') && lo >= 0 ? "+" : "";
            output.Append(sign).Append(lo == hi ? Num(lo) : "(" + Num(lo) + "-" + Num(hi) + ")");
            bindings.Add(new(position, scales[index], Precision: precision[index]));
            end = placeholder.Index + placeholder.Length;
        }
        Literal(variant.Text[end..]);
        return output.ToString().Contains('{') ? null : new(output.ToString(), bindings.ToArray());
    }

    static bool Restrict(string condition, ref int low, ref int high)
    {
        if (condition == "#") return true;
        if (condition.StartsWith('!'))
        {
            int excluded = int.Parse(condition[1..], Invariant);
            if (low == excluded) low++;
            if (high == excluded) high--;
        }
        else if (condition.Split('|') is { Length: 2 } range)
        {
            if (range[0] != "#") low = Math.Max(low, int.Parse(range[0], Invariant));
            if (range[1] != "#") high = Math.Min(high, int.Parse(range[1], Invariant));
        }
        else { int exact = int.Parse(condition, Invariant); low = Math.Max(low, exact); high = Math.Min(high, exact); }
        return low <= high;
    }

    static bool Holds(string condition, double value)
    {
        bool Bound(string s, bool lower) => s == "#" || double.TryParse(s, NumberStyles.Float, Invariant, out var n) && (lower ? value >= n : value <= n);
        if (condition == "#") return true;
        if (condition.StartsWith('!')) return double.TryParse(condition[1..], NumberStyles.Float, Invariant, out var n) && value != n;
        var range = condition.Split('|');
        return range.Length == 2 ? Bound(range[0], true) && Bound(range[1], false) : double.TryParse(condition, NumberStyles.Float, Invariant, out var exact) && value == exact;
    }
}
