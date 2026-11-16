namespace BlackwaterExchange;

/// <summary>An auction house as the website addresses it: eu/2/114.</summary>
public sealed record MarketKey(string Region, int Ruleset, int House)
{
    public override string ToString() => $"{Region}/{Ruleset}/{House}";

    public string Faction => WowForever.FactionOf(House) ?? "";

    public static MarketKey? Parse(string? text)
    {
        var parts = (text ?? "").Split('/');
        if (parts.Length != 3 || !WowForever.Regions.Values.Contains(parts[0])
            || !int.TryParse(parts[1], out var ruleset) || !int.TryParse(parts[2], out var house))
            return null;
        return WowForever.HouseFor(ruleset, WowForever.FactionOf(house) ?? "") == house ? new MarketKey(parts[0], ruleset, house) : null;
    }
}

/// <summary>
/// WoW Forever's realms and auction houses, as the website and the addon know them
/// (AuctionMarket/src/AuctionMarket/ForeverRulesets.cs, ForeverCraftAssistant/Service/MarketDataService.lua).
/// Each ruleset has one shared realm named after it (CAMWOWEU5Realm3PvE) and three houses.
/// </summary>
public static class WowForever
{
    /// <summary>GetCurrentRegion() to the website's region codes.</summary>
    public static readonly IReadOnlyDictionary<int, string> Regions = new Dictionary<int, string>
    {
        [1] = "us", [2] = "kr", [3] = "eu", [4] = "tw",
    };

    private static readonly Dictionary<int, Dictionary<string, int>> Houses = new()
    {
        [1] = new() { ["Alliance"] = 110, ["Horde"] = 113, ["Blackwater"] = 116 },
        [2] = new() { ["Alliance"] = 111, ["Horde"] = 114, ["Blackwater"] = 117 },
        [3] = new() { ["Alliance"] = 119, ["Horde"] = 120, ["Blackwater"] = 121 },
        [15] = new() { ["Alliance"] = 123, ["Horde"] = 124, ["Blackwater"] = 125 },
    };

    public static int? HouseFor(int ruleset, string faction) =>
        Houses.TryGetValue(ruleset, out var houses) && houses.TryGetValue(faction, out var house) ? house : null;

    public static string? FactionOf(int house) =>
        Houses.Values.SelectMany(houses => houses).Where(pair => pair.Value == house).Select(pair => pair.Key).FirstOrDefault();

    /// <summary>The ruleset a realm belongs to, from the end of its name, or null.</summary>
    public static int? RulesetForRealm(string? realm)
    {
        var name = (realm ?? "").ToLowerInvariant();
        if (name.EndsWith("rppvp") || name.EndsWith("rp-pvp"))
            return 15;
        if (name.EndsWith("pvp"))
            return 1;
        if (name.EndsWith("rp"))
            return 3;
        if (name.EndsWith("pve"))
            return 2;
        return null;
    }
}
