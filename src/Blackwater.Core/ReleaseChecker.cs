using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace BlackwaterExchange;

/// <summary>Reads public release metadata. These checks never send device credentials or download installers.</summary>
public sealed class ReleaseChecker(HttpClient http, string appVersion)
{
    public const string GitHubPage = "https://github.com/EPM-Cortez/Blackwater-Exchange-Companion-App/releases";
    public const string CurseForgePage = "https://www.curseforge.com/wow/addons/forever-craft-assistant/files/all";

    private const string GitHubApi = "https://api.github.com/repos/EPM-Cortez/Blackwater-Exchange-Companion-App/releases";
    private const int AddonProjectId = 1788158;
    private const int ForeverFlavorId = 88568;
    private const int ApprovedFileStatus = 4;
    private const int ReleaseFileType = 1;
    private const int PageSize = 10;
    private const int MaxCurseForgePages = 20;
    private const string AddonArchivePrefix = "ForeverCraftAssistant-";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    public async Task<Version?> LatestDesktopAsync(Architecture architecture, CancellationToken cancellationToken)
    {
        var installerSuffix = architecture switch
        {
            Architecture.X64 => "-x64.msi",
            Architecture.Arm64 => "-arm64.msi",
            _ => throw new ArgumentException("No MSI is available for this architecture.", nameof(architecture)),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);
        // The project publishes previews. The single 'latest' GitHub endpoint would hide those releases.
        var releases = await GetAsync<GitHubRelease[]>($"{GitHubApi}?per_page=30", deadline.Token);
        Version? latest = null;
        foreach (var release in releases)
        {
            if (release is not { Draft: false, TagName: { } tag, Assets: { } assets }
                || !tag.StartsWith('v') || ParseReleaseVersion(tag[1..]) is not { } version)
                continue;
            var installer = $"BlackwaterExchange-{version}{installerSuffix}";
            if (version > latest && assets.Any(asset => asset is { State: "uploaded" } && asset.Name == installer))
                latest = version;
        }
        return latest;
    }

    public async Task<Version?> LatestForeverAddonAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);
        Version? latest = null;
        // CurseForge's public project files list identifies the flavor separately from the client patch version.
        for (var page = 0; page < MaxCurseForgePages; page++)
        {
            var url = $"https://www.curseforge.com/api/v1/mods/{AddonProjectId}/files?pageIndex={page}&pageSize={PageSize}";
            var response = await GetAsync<CurseForgeFiles>(url, deadline.Token);
            if (response is not { Data: { } files, Pagination: { } pagination } || pagination.Index != page
                || pagination.PageSize != PageSize || pagination.TotalCount < 0 || (files.Length == 0 && pagination.TotalCount > page * PageSize))
                throw new InvalidDataException("CurseForge returned an incomplete file list.");
            foreach (var file in files)
            {
                if (file is not { ProjectId: AddonProjectId, Status: ApprovedFileStatus, ReleaseType: ReleaseFileType, IsEarlyAccessContent: false,
                    GameVersionTypeIds: { } flavors, FileName: { } name } || !flavors.Contains(ForeverFlavorId)
                    || !name.StartsWith(AddonArchivePrefix, StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                var version = ParseReleaseVersion(name[AddonArchivePrefix.Length..^4]);
                if (version > latest)
                    latest = version;
            }
            if ((page + 1) * PageSize >= pagination.TotalCount)
                return latest;
        }
        throw new InvalidDataException("CurseForge returned too many pages to check.");
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BlackwaterExchangeApp", appVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidDataException("The release list was empty.");
    }

    private static Version? ParseReleaseVersion(string value) => Version.TryParse(value, out var version)
        && version.Build >= 0 && version.Revision < 0 && version.ToString() == value ? version : null;

    private sealed record GitHubRelease(bool? Draft, [property: JsonPropertyName("tag_name")] string? TagName, GitHubAsset?[]? Assets);
    private sealed record GitHubAsset(string? Name, string? State);
    private sealed record CurseForgeFiles(CurseForgeFile?[]? Data, CurseForgePageInfo? Pagination);
    private sealed record CurseForgePageInfo(int? Index, int? PageSize, int? TotalCount);
    private sealed record CurseForgeFile(int ProjectId, int Status, int ReleaseType, bool? IsEarlyAccessContent,
        int[]? GameVersionTypeIds, string? FileName);
}
