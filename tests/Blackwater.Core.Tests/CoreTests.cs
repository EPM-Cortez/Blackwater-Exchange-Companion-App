using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BlackwaterExchange.Tests;

public class CoreTests
{
    [Theory]
    [InlineData("CAMWOWEU5Realm3PvE", 2)]
    [InlineData("CAMWOWEU5Realm1PvP", 1)]
    [InlineData("CAMWOWEU5Realm4RP", 3)]
    [InlineData("CAMWOWEU5Realm5RPPvP", 15)]
    [InlineData("SomeRealm-RP-PvP", 15)]
    [InlineData("Stormrage", null)]
    public void ReadsTheRulesetFromTheRealmName(string realm, int? ruleset) =>
        Assert.Equal(ruleset, WowForever.RulesetForRealm(realm));

    [Fact]
    public void ParsesOnlyRealHouses()
    {
        Assert.Equal(new MarketKey("eu", 2, 114), MarketKey.Parse("eu/2/114"));
        Assert.Equal("Horde", MarketKey.Parse("eu/2/114")!.Faction);
        Assert.Null(MarketKey.Parse("eu/1/114")); // a PvE house under the PvP ruleset
        Assert.Null(MarketKey.Parse("xx/2/114"));
        Assert.Null(MarketKey.Parse("eu/2/999"));
        Assert.Null(MarketKey.Parse("eu/2"));
    }

    [Fact]
    public void KeepsTheDeviceKeyEncryptedForThisWindowsUser()
    {
        using var folder = new TempFolder();
        var key = new DeviceKey(folder.Path);
        var server = ApiClient.ParseServer(AppSettings.DefaultServer);
        Assert.Null(key.Read(server));
        key.Save("secret-device-key-123", server);
        Assert.Equal("secret-device-key-123", key.Read(server));
        Assert.Null(key.Read(ApiClient.ParseServer("https://blackwater.test")));
        Assert.DoesNotContain("secret-device-key", Encoding.UTF8.GetString(File.ReadAllBytes(folder.Combine("device.key"))));
        // A damaged file reads as "not linked", never as a crash.
        File.WriteAllBytes(folder.Combine("device.key"), [1, 2, 3]);
        Assert.Null(key.Read(server));
        key.Delete();
        Assert.False(key.Exists);
    }

    [Fact]
    public void ReadsTheOriginalKeyFormatOnlyForProduction()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder.Combine("device.key"), DeviceKey.Protect("legacy-test-key"u8.ToArray()));
        var key = new DeviceKey(folder.Path);
        Assert.Equal("legacy-test-key", key.Read(ApiClient.ParseServer(AppSettings.DefaultServer)));
        Assert.Null(key.Read(ApiClient.ParseServer("https://localhost:5180")));
    }

    [Fact]
    public void SavesSettingsAndStateAndSurvivesADamagedFile()
    {
        using var folder = new TempFolder();
        var storage = new AppStorage(folder.Path);
        Assert.False(storage.LoadSettings().UploadLedger);
        storage.SaveSettings(new AppSettings { GameFolder = @"S:\World of Warcraft\_forever_", KeepPrices = false });
        Assert.False(storage.LoadSettings().KeepPrices);
        Assert.Equal(@"S:\World of Warcraft\_forever_", storage.LoadSettings().GameFolder);
        storage.SaveState(new AppState { LinkedUser = "Player", UploadedFiles = new() { [@"C:\A.lua"] = "abc" } });
        Assert.Equal("abc", storage.LoadState().UploadedFiles[@"c:\a.lua"]);
        File.WriteAllText(folder.Combine("settings.json"), "{ not json");
        Assert.Equal(AppSettings.DefaultServer, storage.LoadSettings().ServerUrl);
    }

    [Fact]
    public void RefusesAPlainHttpServerUnlessItIsThisPc()
    {
        Assert.Throws<ArgumentException>(() => new ApiClient(new HttpClient(), "http://blackwater.exchange", "1"));
        _ = new ApiClient(new HttpClient(), "http://127.0.0.1:5180", "1");
    }

    [Fact]
    public async Task LinksOnceTheCodeIsApproved()
    {
        var polls = 0;
        var server = new FakeServer((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/devices/start" => FakeServer.Json("""
                {"user_code":"WXZB-2345","device_code":"secret","verification_url":"https://blackwater.test/link",
                 "verification_url_complete":"https://blackwater.test/link?code=WXZB-2345","expires_in":600,"interval":1}
                """),
            "/api/devices/token" => ++polls < 2
                ? FakeServer.Json("""{"status":"pending"}""")
                : FakeServer.Json("""{"status":"approved","token":"device-key","user":{"name":"Player"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        string? shown = null;
        var (token, user) = await FakeServer.Client(server).LinkAsync("Gaming PC", start => shown = start.UserCode, default);
        Assert.Equal(("device-key", "Player", "WXZB-2345"), (token, user, shown));
        var start = server.Requests[0];
        Assert.Contains("\"name\":\"Gaming PC\"", Encoding.UTF8.GetString(start.Body!));
        Assert.Equal("0.1.0", start.Request.Headers.GetValues("X-App-Version").Single());
        Assert.Contains("\"device_code\":\"secret\"", Encoding.UTF8.GetString(server.Requests[1].Body!));
    }

    [Fact]
    public async Task StopsLinkingWhenTheCodeIsRefused()
    {
        var server = new FakeServer((request, _) => request.RequestUri!.AbsolutePath == "/api/devices/start"
            ? FakeServer.Json("""{"user_code":"A","device_code":"d","verification_url":"https://blackwater.test/link","verification_url_complete":"https://blackwater.test/link?code=A","expires_in":600,"interval":1}""")
            : FakeServer.Json("""{"status":"denied"}"""));
        await Assert.ThrowsAsync<LinkFailedException>(() => FakeServer.Client(server).LinkAsync("PC", _ => { }, default));
    }

    [Theory]
    [InlineData("https://blackwater.test/link", true)]
    [InlineData("https://blackwater.test:8443/link", false)]
    [InlineData("http://blackwater.test/link", false)]
    public void ComparesTheEntireWebOrigin(string url, bool allowed) =>
        Assert.Equal(allowed, ApiClient.IsSameOrigin(new Uri("https://blackwater.test"), new Uri(url)));

    [Fact]
    public async Task UploadsEachSavedFileOnlyWhenItChanges()
    {
        using var folder = new TempFolder();
        var file = folder.Combine("ForeverCraftAssistant.lua");
        File.WriteAllText(file, "ForeverCraftAssistantDB = { }");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        var unlinked = false;
        var server = new FakeServer((request, body) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
            if (unlinked)
                return FakeServer.Json("""{"error":"unlinked"}""", HttpStatusCode.Unauthorized);
            return Encoding.UTF8.GetString(body!).Contains("broken")
                ? FakeServer.Json("""{"error":"bad_file","message":"The file could not be read."}""", HttpStatusCode.BadRequest)
                : FakeServer.Json("""{"entriesAdded":12,"salesAdded":3,"balancesAdded":1,"skipped":0}""");
        });
        var uploader = new LedgerUploader(FakeServer.Client(server));

        var (first, state) = await uploader.UploadChangedAsync([file], "key", new AppState(), default);
        Assert.Equal("uploaded", Assert.Single(first).Outcome);
        Assert.Equal(12, first[0].Result!.EntriesAdded);
        Assert.NotNull(state.LastUploadAt);

        var (again, _) = await uploader.UploadChangedAsync([file], "key", state, default);
        Assert.Equal("unchanged", Assert.Single(again).Outcome);
        Assert.Single(server.Requests);

        // Still being written: wait for it to settle.
        File.WriteAllText(file, "ForeverCraftAssistantDB = { broken");
        var (settling, _) = await uploader.UploadChangedAsync([file], "key", state, default);
        Assert.Equal("settling", Assert.Single(settling).Outcome);

        // A file the website refuses is not sent again until the game writes a new one.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        var (refused, afterRefusal) = await uploader.UploadChangedAsync([file], "key", state, default);
        Assert.Equal(("refused", "The file could not be read."), (refused[0].Outcome, refused[0].Problem));
        var (skipped, _) = await uploader.UploadChangedAsync([file], "key", afterRefusal, default);
        Assert.Equal("unchanged", skipped[0].Outcome);

        // A removed key stops uploading.
        File.WriteAllText(file, "ForeverCraftAssistantDB = { new = 1 }");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        unlinked = true;
        await Assert.ThrowsAsync<UnlinkedException>(() => uploader.UploadChangedAsync([file], "key", afterRefusal, default));
    }

    private const string SavedFile = """
        ForeverCraftAssistantDB = {
            ["characters"] = {
                ["Tess Ironfoot-CAMWOWEU5Realm3PvE"] = { ["name"] = "Tess Ironfoot", ["realm"] = "CAMWOWEU5Realm3PvE", ["faction"] = "Horde", ["region"] = 3, ["recipes"] = { 1, 2, 3 } },
                ["Ed Fearan-CAMWOWEU5Realm3PvE"] = { ["realm"] = "CAMWOWEU5Realm3PvE", ["faction"] = "Alliance" },
                ["Pvp Guy-CAMWOWEU5Realm1PvP"] = { ["realm"] = "CAMWOWEU5Realm1PvP", ["faction"] = "Horde", ["region"] = 3 },
                ["Old-Elsewhere"] = { ["realm"] = "Stormrage", ["faction"] = "Horde", ["region"] = 3 },
            },
            ["ledger"] = { ["region"] = 3 },
        }
        """;

    [Fact]
    public void WorksOutTheHousesFromTheCharacters()
    {
        using var folder = new TempFolder();
        var file = folder.Combine("ForeverCraftAssistant.lua");
        File.WriteAllText(file, SavedFile);
        // Ed has no region of his own (saved before 1.7.1); the account's ledger region covers him.
        Assert.Equal(["eu/1/113", "eu/1/116", "eu/2/111", "eu/2/114", "eu/2/117"],
            PriceUpdater.HousesFor([file, folder.Combine("missing.lua")]).Select(house => house.ToString()));
    }

    private static string Export(string market, long captured, string items = """[{"id":2589,"units":400,"minimum_ask_copper":9,"average_ask_copper":11.5,"removed_24h":120,"early_removed_24h":20,"early_removal_average_ask_copper":10}]""") =>
        $$"""{"schema":1,"market":"{{market}}","captured_at":{{captured}},"expires_at":{{captured + 21600}},"items":{{items}}}""";

    [Fact]
    public void WritesAPriceFileTheAddonCanRead()
    {
        var now = DateTimeOffset.UtcNow;
        var captured = now.AddMinutes(-20).ToUnixTimeSeconds();
        var writer = new MarketDataWriter();
        Assert.True(writer.Add(new MarketKey("eu", 2, 114), JsonDocument.Parse(Export("eu/2/114", captured)).RootElement, now));
        Assert.True(writer.Add(new MarketKey("eu", 2, 117), JsonDocument.Parse(Export("eu/2/117", captured, "[]")).RootElement, now));
        // Stale, or for another house: left out.
        Assert.False(writer.Add(new MarketKey("eu", 2, 111), JsonDocument.Parse(Export("eu/2/111", captured - 30000)).RootElement, now));
        Assert.False(writer.Add(new MarketKey("eu", 2, 111), JsonDocument.Parse(Export("eu/2/114", captured)).RootElement, now));
        // Values that are not numbers never reach the file.
        Assert.Throws<FormatException>(() => writer.Add(new MarketKey("eu", 2, 111),
            JsonDocument.Parse(Export("eu/2/111", captured, """[{"id":1,"units":"1; os.exit()"}]""")).RootElement, now));
        Assert.Throws<FormatException>(() => writer.Add(new MarketKey("eu", 2, 111),
            JsonDocument.Parse(Export("eu/2/111", captured, """[{"id":1,"units":1},{"id":1,"units":2}]""")).RootElement, now));

        var lua = writer.ToLua();
        var root = (LuaTable)LuaSavedVariables.Parse(lua)["ForeverCraftAssistantMarketData"]!;
        Assert.Equal(2, root.Number("schema"));
        var horde = root.Table("markets")!.Table("eu/2/114")!;
        Assert.Equal("Horde", horde.String("faction"));
        Assert.Equal((double)captured, horde.Number("capturedAt"));
        var linen = (LuaTable)horde.Table("items")![2589]!;
        Assert.Equal((400.0, 9.0, 11.5, 10.0), (linen.Number("units")!.Value, linen.Number("min")!.Value, linen.Number("avg")!.Value,
            linen.Number("earlyAsk")!.Value));
        Assert.Equal("Blackwater", root.Table("markets")!.Table("eu/2/117")!.String("faction"));
        Assert.Null(root.Table("markets")!.Table("eu/2/111"));
    }

    [Fact]
    public async Task RewritesPricesOnlyWhenTheWebsiteHasANewerScan()
    {
        using var folder = new TempFolder();
        var game = new GameFolder(Directory.CreateDirectory(folder.Combine("_forever_", "Interface", "AddOns")).Parent!.Parent!.FullName);
        var scan = DateTimeOffset.UtcNow.AddMinutes(-20);
        var exports = 0;
        var server = new FakeServer((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.Equal("Bearer device-fixture", request.Headers.Authorization?.ToString());
            if (path == "/api/devices/me/markets")
                return FakeServer.Json($$"""
                    [{"region":"eu","ruleset_id":2,"house_id":114,"name":"Horde","captured_at":"{{scan:O}}"},
                     {"region":"eu","ruleset_id":2,"house_id":117,"name":"Blackwater","captured_at":"{{scan:O}}"},
                     {"region":"eu","ruleset_id":1,"house_id":113,"name":"Old","captured_at":"{{scan.AddDays(-2):O}}"}]
                    """);
            exports++;
            Assert.EndsWith("/addon", path);
            var market = string.Join('/', path.Split('/')[5..8]);
            return FakeServer.Json(Export(market, scan.ToUnixTimeSeconds()));
        });
        var updater = new PriceUpdater(FakeServer.Client(server));
        MarketKey[] houses = [new("eu", 2, 114), new("eu", 2, 117), new("eu", 1, 113)];

        var first = await updater.UpdateAsync(game, houses, new AppState(), "device-fixture", default);
        Assert.Equal("written", first.Result);
        Assert.Equal(["eu/2/114", "eu/2/117"], first.Written);
        Assert.Equal(["eu/1/113"], first.Skipped); // no scan for two days: not even downloaded
        Assert.Equal(2, exports);
        var file = Path.Combine(game.AddOns, "ForeverCraftAssistantMarketData", "MarketData.lua");
        Assert.Contains("[\"eu/2/114\"] = { faction = \"Horde\"", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(game.AddOns, "ForeverCraftAssistantMarketData", "ForeverCraftAssistantMarketData.toc")));

        var second = await updater.UpdateAsync(game, houses, first.State, "device-fixture", default);
        Assert.Equal("up_to_date", second.Result);
        Assert.Equal(2, exports);

        scan = scan.AddMinutes(10);
        var third = await updater.UpdateAsync(game, houses, first.State, "device-fixture", default);
        Assert.Equal("written", third.Result);
        Assert.Equal(4, exports);

        Assert.Equal("no_characters", (await updater.UpdateAsync(game, [], first.State, "device-fixture", default)).Result);
    }
}
