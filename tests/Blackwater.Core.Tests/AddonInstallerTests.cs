using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace BlackwaterExchange.Tests;

public class AddonInstallerTests
{
    [Theory]
    [InlineData("1.7.2", "1.7.1", true)]
    [InlineData("1.10.0", "1.9.3", true)]
    [InlineData("1.7.1", "1.7.1", false)]
    [InlineData("1.7.0", "1.7.1", false)]
    [InlineData("1.8.0", "1.8.0-beta", true)]
    [InlineData("1.8.0-beta", "1.8.0", false)]
    [InlineData("2", "1.9", true)]
    [InlineData("1.0.0", null, true)]
    public void ComparesVersions(string latest, string? installed, bool newer) =>
        Assert.Equal(newer, AddonInstaller.IsNewer(latest, installed));

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        return memory.ToArray();
    }

    private static (string, string)[] Release(string version) =>
    [
        ("ForeverCraftAssistant/ForeverCraftAssistant.toc", $"## Interface: 16001\n## Version: {version}\nCore\\Namespace.lua\n"),
        ("ForeverCraftAssistant/Core/Namespace.lua", $"-- {version}"),
    ];

    [Fact]
    public void InstallsAndReplacesTheAddon()
    {
        using var folder = new TempFolder();
        var addOns = Directory.CreateDirectory(folder.Combine("AddOns")).FullName;
        var zip = folder.Combine("release.zip");

        File.WriteAllBytes(zip, Zip(Release("1.7.1")));
        AddonInstaller.Install(zip, addOns);
        Assert.Equal("-- 1.7.1", File.ReadAllText(Path.Combine(addOns, "ForeverCraftAssistant", "Core", "Namespace.lua")));

        // A file the new version no longer has goes; other addons are untouched.
        File.WriteAllText(Path.Combine(addOns, "ForeverCraftAssistant", "Removed.lua"), "old");
        Directory.CreateDirectory(Path.Combine(addOns, "ForeverCraftAssistantMarketData"));
        var previousBackup = Directory.CreateDirectory(Path.Combine(addOns, ".ForeverCraftAssistant.old-backup"));
        File.WriteAllBytes(zip, Zip(Release("1.7.2")));
        AddonInstaller.Install(zip, addOns);
        Assert.Equal("-- 1.7.2", File.ReadAllText(Path.Combine(addOns, "ForeverCraftAssistant", "Core", "Namespace.lua")));
        Assert.False(File.Exists(Path.Combine(addOns, "ForeverCraftAssistant", "Removed.lua")));
        Assert.True(Directory.Exists(Path.Combine(addOns, "ForeverCraftAssistantMarketData")));
        Assert.Equal([previousBackup.Name, "ForeverCraftAssistant", "ForeverCraftAssistantMarketData"],
            Directory.GetDirectories(addOns).Select(Path.GetFileName).Order());
        Assert.Contains("## Version: 1.7.2", File.ReadAllText(Path.Combine(addOns, "ForeverCraftAssistant", "ForeverCraftAssistant.toc")));
    }

    [Theory]
    [InlineData("ForeverCraftAssistant/../Evil/Evil.toc")]
    [InlineData("OtherAddon/OtherAddon.toc")]
    [InlineData("ForeverCraftAssistant/C:/Windows/evil.dll")]
    [InlineData("ForeverCraftAssistant/sub/../../escape.lua")]
    public void RefusesFilesOutsideTheAddonFolder(string name)
    {
        using var folder = new TempFolder();
        var addOns = Directory.CreateDirectory(folder.Combine("AddOns")).FullName;
        var zip = folder.Combine("release.zip");
        File.WriteAllBytes(zip, Zip([.. Release("1.7.1"), (name, "evil")]));
        Assert.Throws<InvalidDataException>(() => AddonInstaller.Install(zip, addOns));
        // Nothing installed and nothing left behind.
        Assert.Empty(Directory.GetDirectories(addOns));
        Assert.False(File.Exists(folder.Combine("Evil", "Evil.toc")));
    }

    [Fact]
    public void KeepsTheInstalledCopyWhenTheReleaseHasNoToc()
    {
        using var folder = new TempFolder();
        var addOns = Directory.CreateDirectory(folder.Combine("AddOns")).FullName;
        var zip = folder.Combine("release.zip");
        File.WriteAllBytes(zip, Zip(Release("1.7.1")));
        AddonInstaller.Install(zip, addOns);
        File.WriteAllBytes(zip, Zip(("ForeverCraftAssistant/Core/Namespace.lua", "-- broken")));
        Assert.Throws<InvalidDataException>(() => AddonInstaller.Install(zip, addOns));
        Assert.Equal("-- 1.7.1", File.ReadAllText(Path.Combine(addOns, "ForeverCraftAssistant", "Core", "Namespace.lua")));
        Assert.Single(Directory.GetDirectories(addOns));
    }

    [Fact]
    public async Task DownloadsChecksAndInstallsOnlyANewerMatchingRelease()
    {
        using var folder = new TempFolder();
        var game = new GameFolder(folder.Combine("_forever_"));
        Directory.CreateDirectory(game.AddOns);
        var zip = Zip(Release("1.7.2"));
        var sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        var listedSha = sha;
        var downloadHost = "blackwater.test";
        var server = new FakeServer((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/addon/latest" => FakeServer.Json($$"""
                {"version":"1.7.2","url":"https://{{downloadHost}}/downloads/addon/ForeverCraftAssistant-1.7.2.zip","sha256":"{{listedSha}}","size":{{zip.Length}}}
                """),
            "/downloads/addon/ForeverCraftAssistant-1.7.2.zip" => FakeServer.Bytes(zip),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var installer = new AddonInstaller(FakeServer.Client(server), folder.Combine("work"));

        // A download that does not match the listed hash is thrown away.
        listedSha = new string('0', 64);
        var bad = await installer.UpdateAsync(game, default);
        Assert.Equal(("bad_download", null), (bad.Result, bad.Installed));
        Assert.False(Directory.Exists(Path.Combine(game.AddOns, "ForeverCraftAssistant")));

        // A release on another server is never downloaded.
        listedSha = sha;
        downloadHost = "evil.example";
        await Assert.ThrowsAsync<ApiException>(() => installer.UpdateAsync(game, default));

        downloadHost = "blackwater.test";
        var installed = await installer.UpdateAsync(game, default);
        Assert.Equal(("installed", "1.7.2"), (installed.Result, installed.Installed));
        Assert.Equal("up_to_date", (await installer.UpdateAsync(game, default)).Result);
        Assert.Empty(Directory.GetFiles(folder.Combine("work")));
    }
}
