using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BlackwaterExchange;

/// <summary>
/// Keeps the ForeverCraftAssistantMarketData addon filled with Blackwater Exchange prices for every house the
/// player trades on: each character's own faction house and the Blackwater house of its ruleset, worked out from
/// Forever Craft Assistant's saved files. The file is rewritten only when the website has a newer scan.
/// The addon picks the right houses for whoever is logged in (Service/MarketDataService.lua).
/// </summary>
public sealed class PriceUpdater(ApiClient api, Func<DateTimeOffset>? clock = null)
{
    private const string Variable = "ForeverCraftAssistantDB";
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The houses for the characters in these saved files, sorted.</summary>
    public static IReadOnlyList<MarketKey> HousesFor(IEnumerable<string> savedFiles)
    {
        var houses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in savedFiles)
        {
            Dictionary<string, object?> variables;
            try
            {
                using var stream = GameFolder.OpenSavedFile(file);
                var length = stream.Length;
                if (length > LedgerUploader.MaxBytes)
                    continue;
                var bytes = new byte[length];
                stream.ReadExactly(bytes);
                using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
                variables = LuaSavedVariables.Parse(reader.ReadToEnd(), path => path switch
                {
                    [Variable] => true,
                    [Variable, "characters" or "ledger"] => true,
                    [Variable, "characters", _] => true,
                    [Variable, "characters", _, "realm" or "faction" or "region"] => true,
                    [Variable, "ledger", "region"] => true,
                    _ => false,
                });
            }
            catch (Exception exception) when (exception is IOException or LuaFormatException or UnauthorizedAccessException)
            {
                continue;
            }
            if (variables.GetValueOrDefault(Variable) is not LuaTable root)
                continue;
            var accountRegion = root.Table("ledger")?.Number("region");
            foreach (var value in root.Table("characters")?.Fields.Values ?? Enumerable.Empty<object?>())
            {
                if (value is not LuaTable character)
                    continue;
                var regionNumber = character.Number("region") ?? accountRegion;
                if (regionNumber is not { } number || !WowForever.Regions.TryGetValue((int)number, out var region)
                    || WowForever.RulesetForRealm(character.String("realm")) is not { } ruleset)
                    continue;
                if (WowForever.HouseFor(ruleset, character.String("faction") ?? "") is { } own)
                    houses.Add(new MarketKey(region, ruleset, own).ToString());
                houses.Add(new MarketKey(region, ruleset, WowForever.HouseFor(ruleset, "Blackwater")!.Value).ToString());
            }
        }
        return houses.Select(key => MarketKey.Parse(key)!).ToList();
    }

    public sealed record Outcome(string Result, IReadOnlyList<string> Written, IReadOnlyList<string> Skipped, AppState State);

    /// <summary>
    /// Writes the price file for <paramref name="houses"/> when any has a newer scan than the installed file (or the
    /// file is missing or holds other houses). Houses without a fresh scan are left out; with none fresh, the
    /// installed file stays as it is.
    /// </summary>
    public async Task<Outcome> UpdateAsync(GameFolder game, IReadOnlyList<MarketKey> houses, AppState state, CancellationToken cancellationToken)
    {
        if (houses.Count == 0)
            return new Outcome("no_characters", [], [], state);
        var folder = Path.Combine(game.AddOns, GameFolder.MarketDataAddonName);
        var file = Path.Combine(folder, "MarketData.lua");
        var toc = Path.Combine(folder, GameFolder.MarketDataAddonName + ".toc");
        game.RequireOrdinaryPath(file);
        // Only houses the website has scanned in the last six hours; the rest are left out until it has.
        var listed = (await api.MarketsAsync(cancellationToken)).ToDictionary(market => market.Key.ToString());
        var available = houses.Where(house => listed.TryGetValue(house.ToString(), out var market)
            && market.CapturedAt is { } captured && now() - captured < TimeSpan.FromHours(6)).ToList();
        var unavailable = houses.Except(available).Select(house => house.ToString()).ToList();
        if (available.Count == 0)
            return new Outcome("nothing_fresh", [], unavailable, state);
        var wanted = available.Select(house => house.ToString()).ToHashSet();
        var upToDate = File.Exists(file) && wanted.SetEquals(state.PriceCaptures.Keys)
            // The export gives whole seconds; the list gives fractions.
            && wanted.All(key => listed.TryGetValue(key, out var market) && market.CapturedAt is { } captured
                && state.PriceCaptures[key].ToUnixTimeSeconds() >= captured.ToUnixTimeSeconds());
        if (upToDate)
            return new Outcome("up_to_date", [], unavailable, state);

        var writer = new MarketDataWriter();
        var captures = new Dictionary<string, DateTimeOffset>();
        var skipped = new List<string>(unavailable);
        foreach (var house in available)
        {
            using var export = await api.MarketExportAsync(house, cancellationToken);
            if (export is null || !writer.Add(house, export.RootElement, now()))
            {
                skipped.Add(house.ToString());
                continue;
            }
            captures[house.ToString()] = DateTimeOffset.FromUnixTimeSeconds(export.RootElement.GetProperty("captured_at").GetInt64());
        }
        if (captures.Count == 0)
            return new Outcome("nothing_fresh", [], skipped, state);

        game.RequireOrdinaryPath(file);
        game.RequireOrdinaryPath(toc);
        Directory.CreateDirectory(folder);
        AppStorage.WriteAtomically(file, Encoding.UTF8.GetBytes(writer.ToLua()));
        if (!File.Exists(toc))
            AppStorage.WriteAtomically(toc, Encoding.UTF8.GetBytes(
                "## Interface: 16001\n## Title: Forever Craft Assistant Market Data\n## Notes: Auction prices from Blackwater Exchange, kept up to date by the Blackwater Exchange app.\nMarketData.lua\n"));
        return new Outcome("written", [.. captures.Keys], skipped, state with { PriceCaptures = captures, LastPricesAt = now() });
    }
}

/// <summary>
/// Turns the website's price exports into the addon's MarketData.lua (schema 2). Every value is checked and
/// written as a Lua number; the only strings are house keys and factions from <see cref="WowForever"/>, so the
/// file can never contain anything but data.
/// </summary>
public sealed class MarketDataWriter
{
    private readonly SortedDictionary<string, string> markets = new(StringComparer.Ordinal);

    /// <summary>Adds one house's export. False when it is not a fresh export of that house.</summary>
    public bool Add(MarketKey house, JsonElement export, DateTimeOffset now)
    {
        if (MarketKey.Parse(house.ToString()) != house || export.ValueKind != JsonValueKind.Object
            || !export.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1
            || !export.TryGetProperty("market", out var market) || market.ValueKind != JsonValueKind.String
            || market.GetString() != house.ToString()
            || !export.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return false;
        var captured = Integer(export, "captured_at");
        var expires = Integer(export, "expires_at");
        var seconds = now.ToUnixTimeSeconds();
        if (captured is null || expires is null || captured > seconds + 300 || expires <= seconds
            || expires <= captured || expires > captured + 6 * 3600)
            return false;

        var lua = new StringBuilder();
        lua.Append(CultureInfo.InvariantCulture,
            $"        [\"{house}\"] = {{ faction = \"{house.Faction}\", capturedAt = {captured}, expiresAt = {expires}, items = {{\n");
        var seen = new HashSet<long>();
        foreach (var item in items.EnumerateArray())
        {
            var id = Integer(item, "id");
            if (id is not (>= 1 and <= int.MaxValue) || !seen.Add(id.Value) || Integer(item, "units") is not { } units)
                throw new FormatException($"Invalid or duplicate item in the {house} prices.");
            lua.Append(CultureInfo.InvariantCulture,
                $"            [{id}] = {{ units = {units}, min = {Number(item, "minimum_ask_copper")}, avg = {Number(item, "average_ask_copper")}, removed = {Lua(Integer(item, "removed_24h"))}, early = {Lua(Integer(item, "early_removed_24h"))}, earlyAsk = {Number(item, "early_removal_average_ask_copper")} }},\n");
        }
        lua.Append("        } },\n");
        markets[house.ToString()] = lua.ToString();
        return true;
    }

    public int Count => markets.Count;

    public string ToLua()
    {
        var lua = new StringBuilder("-- Written by the Blackwater Exchange app. Changes here are replaced on the next update.\n");
        lua.Append("ForeverCraftAssistantMarketData = {\n    schema = 2,\n    markets = {\n");
        foreach (var market in markets.Values)
            lua.Append(market);
        lua.Append("    },\n}\n");
        return lua.ToString();
    }

    private const decimal MaxNumber = 9007199254740991m;

    private static long? Integer(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0 || number > MaxNumber
            || number != decimal.Truncate(number))
            throw new FormatException($"{name} is not a whole number.");
        return (long)number;
    }

    private static string Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return "nil";
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0 || number > MaxNumber)
            throw new FormatException($"{name} is not a price.");
        return Math.Round(number, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string Lua(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "nil";
}
