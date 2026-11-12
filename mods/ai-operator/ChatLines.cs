namespace LocalExile.Mods.AiOperator;

/// <summary>
/// Splits one JSON document into the chat lines the client shows, the way <c>gm-commands</c> splits <c>/state</c>:
/// the first line carries <see cref="Prefix"/>, the continuations <see cref="ContinuationPrefix"/>, and no line is
/// longer than <see cref="LineLength"/> characters plus its prefix. A line never ends between a surrogate pair, so
/// an astral character (an emoji) is never cut in half.
/// </summary>
internal static class ChatLines
{
    /// <summary>The most JSON characters one line carries (the prefix is added on top, as in gm-commands).</summary>
    public const int LineLength = 900;

    /// <summary>What the first line starts with: the outside process keys on it.</summary>
    public const string Prefix = "AI ";

    /// <summary>What every continuation line starts with.</summary>
    public const string ContinuationPrefix = "AI+ ";

    public static IReadOnlyList<string> Split(string json, int length = LineLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        if (json.Length == 0) return [Prefix];

        var lines = new List<string>();
        int at = 0;
        do
        {
            int take = Math.Min(length, json.Length - at);
            // Never end a line between a surrogate pair; a line always takes at least one character (a length of 1,
            // or a lone high surrogate, still moves on).
            if (take < json.Length - at && char.IsHighSurrogate(json[at + take - 1])) take = Math.Max(1, take - 1);
            lines.Add((lines.Count == 0 ? Prefix : ContinuationPrefix) + json.Substring(at, take));
            at += take;
        } while (at < json.Length);
        return lines;
    }
}
