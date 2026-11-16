using System.Net;
using System.Runtime.InteropServices;
using Xunit;

namespace BlackwaterExchange.Tests;

public sealed class ReleaseCheckerTests
{
    [Theory]
    [InlineData(Architecture.X64, "0.1.10")]
    [InlineData(Architecture.Arm64, "0.1.9")]
    public async Task FindsTheNewestPublishedVersionWithAnInstallerForTheRunningArchitecture(Architecture architecture, string expected)
    {
        using var server = new FakeServer((request, _) =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return FakeServer.Json("""
                [
                  {"draft":false,"tag_name":"v0.1.9","assets":[{"name":"BlackwaterExchange-0.1.9-arm64.msi","state":"uploaded"}]},
                  {"draft":false,"tag_name":"nightly","assets":[]},
                  {"draft":true,"tag_name":"v9.0.0","assets":[{"name":"BlackwaterExchange-9.0.0-x64.msi","state":"uploaded"}]},
                  {"draft":false,"tag_name":"v0.1.8","assets":[{"name":"BlackwaterExchange-0.1.8-x64.msi","state":"uploaded"}]},
                  {"draft":false,"tag_name":"v0.1.10","prerelease":true,"assets":[
                    {"name":"BlackwaterExchange-0.1.10-arm64.msi","state":"new"},
                    {"name":"BlackwaterExchange-0.1.10-x64.msi","state":"uploaded"}]}
                ]
                """);
        });
        using var http = new HttpClient(server);
        var checker = new ReleaseChecker(http, "0.1.2");
        Assert.Equal(Version.Parse(expected), await checker.LatestDesktopAsync(architecture, default));
    }

    [Fact]
    public async Task ReadsCurseForgePagesAndFiltersToPublishedForeverReleases()
    {
        using var server = new FakeServer((request, _) =>
        {
            Assert.Equal("www.curseforge.com", request.RequestUri!.Host);
            Assert.EndsWith("/1788158/files", request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return request.RequestUri.Query.Contains("pageIndex=0", StringComparison.Ordinal)
                ? FakeServer.Json("""
                    {"data":[
                      {"projectId":1788158,"status":4,"releaseType":1,"isEarlyAccessContent":false,"gameVersionTypeIds":[88568],"fileName":"ForeverCraftAssistant-1.3.2.zip"},
                      {"projectId":1788158,"status":4,"releaseType":1,"isEarlyAccessContent":false,"gameVersionTypeIds":[517],"fileName":"ForeverCraftAssistant-9.0.0.zip"},
                      {"projectId":1788158,"status":4,"releaseType":2,"isEarlyAccessContent":false,"gameVersionTypeIds":[88568],"fileName":"ForeverCraftAssistant-8.0.0.zip"},
                      {"projectId":1788158,"status":4,"releaseType":1,"isEarlyAccessContent":true,"gameVersionTypeIds":[88568],"fileName":"ForeverCraftAssistant-7.0.0.zip"}
                    ],"pagination":{"index":0,"pageSize":10,"totalCount":11}}
                    """)
                : FakeServer.Json("""
                    {"data":[
                      {"projectId":1788158,"status":4,"releaseType":1,"isEarlyAccessContent":false,"gameVersionTypeIds":[88568],"fileName":"ForeverCraftAssistant-1.7.4.zip"}
                    ],"pagination":{"index":1,"pageSize":10,"totalCount":11}}
                    """);
        });
        using var http = new HttpClient(server);
        var checker = new ReleaseChecker(http, "0.1.2");
        var latest = await checker.LatestForeverAddonAsync(default);
        Assert.Equal(new Version(1, 7, 4), latest);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task ReportsAnUnavailableProviderInsteadOfReturningAnEmptyReleaseList()
    {
        using var http = new HttpClient(new FakeServer((_, _) => FakeServer.Json("{}", HttpStatusCode.Forbidden)));
        var checker = new ReleaseChecker(http, "0.1.2");
        await Assert.ThrowsAsync<HttpRequestException>(() => checker.LatestForeverAddonAsync(default));
    }
}
