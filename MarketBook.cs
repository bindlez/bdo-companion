using System.Text.Json.Serialization;

internal sealed class MarketBook
{
    private const int MaxQuotesPerRegion = 10_000;
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<QuoteKey, MarketQuote>> regions = new()
    {
        ["NA"] = new(),
        ["EU"] = new()
    };
    private string activeRegion = "NA";

    public bool TryImport(MarketImport? import, out int received)
    {
        received = 0;
        var region = NormalizeRegion(import?.Region);
        if (region is null || import?.Items is not { Length: <= 2000 } items)
            return false;

        var stamp = DateTimeOffset.UtcNow;
        var batch = new Dictionary<QuoteKey, MarketQuote>();
        foreach (var item in items)
        {
            if (item?.Name?.Trim() is not { Length: > 0 and <= 100 } name ||
                item.Id is (<= 0 or >= 10_000_000) || item.Price is not (>= 0 and <= 1_000_000_000_000))
                return false;
            if (item.Price > 0)
            {
                var key = item.Id is { } id ? new QuoteKey(id, "") : new QuoteKey(null, name.ToUpperInvariant());
                batch[key] = new MarketQuote(item.Id, name, item.Price.Value, region, stamp);
            }
        }

        lock (gate)
        {
            var quotes = regions[region];
            if (quotes.Count + batch.Keys.Count(id => !quotes.ContainsKey(id)) > MaxQuotesPerRegion)
                return false;
            foreach (var (id, quote) in batch)
                quotes[id] = quote;
            activeRegion = region;
        }
        received = batch.Count;
        return true;
    }

    public MarketQuotes? Lookup(RecipeLookup? lookup)
    {
        var region = NormalizeRegion(lookup?.Region);
        if (lookup is null || region is null) return null;

        lock (gate)
        {
            var quotes = regions[region];
            if (lookup.Ids is { Length: > 0 and <= 64 } ids && lookup.Names is null &&
                ids.All(id => id is > 0 and < 10_000_000))
                return new MarketQuotes(ids.Distinct().Select(id => new QuoteKey(id, ""))
                    .Where(quotes.ContainsKey).Select(key => quotes[key]).ToArray(), region);

            if (lookup.Names is not { Length: > 0 and <= 64 } names || lookup.Ids is not null ||
                names.Any(name => name?.Trim() is not { Length: > 0 and <= 100 }))
                return null;

            var wanted = names.Select(name => name!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = UnambiguousNames(quotes.Values.Where(quote => wanted.Contains(quote.Name))).ToArray();
            return new MarketQuotes(matches, region);
        }
    }

    public PriceSnapshot Snapshot()
    {
        lock (gate)
        {
            var prices = UnambiguousNames(regions[activeRegion].Values).ToDictionary(quote => quote.Name,
                quote => new PriceEntry(quote.Price, quote.Source, quote.UpdatedAt, quote.Name, quote.Region, quote.Id));
            return new PriceSnapshot(prices);
        }
    }

    private static IEnumerable<MarketQuote> UnambiguousNames(IEnumerable<MarketQuote> quotes)
    {
        foreach (var group in quotes.GroupBy(quote => quote.Name, StringComparer.OrdinalIgnoreCase))
        {
            var identified = group.Where(quote => quote.Id.HasValue).Take(2).ToArray();
            if (identified.Length == 0) yield return group.First();
            if (identified.Length == 1) yield return identified[0];
        }
    }

    private readonly record struct QuoteKey(int? Id, string Name);

    private static string? NormalizeRegion(string? region) => region?.ToUpperInvariant() switch
    {
        "NA" => "NA",
        "EU" => "EU",
        _ => null
    };
}

internal sealed record MarketImport(string? Region, MarketImportItem?[]? Items);
internal sealed record MarketImportItem(int? Id, string? Name, long? Price);
internal sealed record MarketImportResult(bool Ok, int QuotesReceived);
internal sealed record RecipeLookup(string? Region, int[]? Ids, string?[]? Names);
internal sealed record MarketQuotes(MarketQuote[] Items, string Region);
internal sealed record MarketQuote(int? Id, string Name, long Price, string Region, DateTimeOffset UpdatedAt)
{
    public string Source => "Browser market cache";
}
internal sealed record PriceSnapshot(Dictionary<string, PriceEntry> Prices);
internal sealed record PriceEntry(long UnitPrice, string Source, DateTimeOffset UpdatedAt, string MarketName, string Region, int? ItemId);
