using System.Globalization;
using System.Text.RegularExpressions;

internal sealed record LootRow(string Item, int Quantity, string Kind, string Stamp);
internal sealed record GuildReading(bool Enabled, bool Available, int? Current, int? Target, string? Label, double? LastAt);
internal sealed record QuestCounter(string Label, int Current, int Target);

internal static partial class FeedReader
{
    public static LootRow[]? ParseLoot(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var rows = new List<LootRow>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 220) return null;
            var match = PersonalLoot().Match(line);
            if (!match.Success) return null;
            var count = match.Groups["count"];
            var quantity = 1;
            if (count.Success && (!TryCount(count.Value, 1_000_000, out quantity) || quantity == 0)) return null;
            var item = match.Groups["item"].Value.Trim();
            if (item.Length is < 1 or > 120 || !item.Any(char.IsLetter) || item.Any(char.IsControl))
                return null;
            var action = match.Groups["action"].Value;
            var crafted = action.Equals("crafted", StringComparison.OrdinalIgnoreCase) ||
                action.Equals("produced", StringComparison.OrdinalIgnoreCase);
            rows.Add(new LootRow(item, quantity, crafted ? "Crafted" : "Drop", match.Groups["stamp"].Value));
        }
        return rows.Count is > 0 and <= 32 ? rows.ToArray() : null;
    }

    public static LootRow[]? NewRows(LootRow[] previous, LootRow[] current)
    {
        if (previous.AsSpan().SequenceEqual(current)) return [];
        var overlap = 0;
        for (var length = 1; length <= Math.Min(previous.Length, current.Length); length++)
        {
            if (!previous.AsSpan(previous.Length - length).SequenceEqual(current.AsSpan(0, length))) continue;
            if (overlap != 0) return null;
            overlap = length;
        }
        return overlap == 0 ? null : current[overlap..];
    }

    public static QuestCounter? ParseGuild(string? text)
    {
        var match = GuildCounter().Match(text?.Trim() ?? "");
        if (!match.Success || !TryCount(match.Groups["current"].Value, 10_000_000, out var current) ||
            !TryCount(match.Groups["target"].Value, 10_000_000, out var target) || target == 0 || current > target)
            return null;
        return new QuestCounter(match.Groups["label"].Value.Trim(), current, target);
    }

    private static bool TryCount(string text, int max, out int count) =>
        int.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture,
            out count) && count <= max;

    private const string Count = @"(?:0|[1-9][0-9]{0,7}|[1-9][0-9]{0,2}(?:,[0-9]{3}){1,2})";

    [GeneratedRegex(@"\A(?:\[?(?<stamp>[0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)\]?\s+)?(?:\[System\]\s*)?You\s+(?:have\s+)?(?<action>obtained|received|acquired|crafted|produced)\s*:?\s*\[(?<item>[^\[\]\r\n]+)\](?:\s*[x×]\s*(?<count>" + Count + @"))?\s*[.!]?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonalLoot();

    [GeneratedRegex(@"\A(?<label>Guild (?:Mission|Quest)[^\r\n/]{0,100})\r?\n\s*(?<current>" + Count + @")\s*/\s*(?<target>" + Count + @")\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuildCounter();
}
