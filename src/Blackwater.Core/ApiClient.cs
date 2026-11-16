using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackwaterExchange;

/// <summary>The website said the device key is not (or no longer) linked to an account.</summary>
public sealed class UnlinkedException() : Exception("This PC is no longer linked to a Blackwater Exchange account.");

/// <summary>The website refused a request, with its own message when it gave one.</summary>
public sealed class ApiException(HttpStatusCode status, string? error, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? Error { get; } = error;
    public bool RateLimited => Status == HttpStatusCode.TooManyRequests;
}

public sealed record LinkStart(
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("verification_url")] string VerificationUrl,
    [property: JsonPropertyName("verification_url_complete")] string VerificationUrlComplete,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

public enum LinkStatus { Pending, Approved, Denied, Expired, Used, Unknown }

public sealed record LinkPoll(LinkStatus Status, string? Token, string? UserName);

public sealed record LinkedAccount(long DeviceId, string DeviceName, string UserName, bool ShareSales);

public sealed record UploadResult(
    [property: JsonPropertyName("entriesAdded")] int EntriesAdded,
    [property: JsonPropertyName("salesAdded")] int SalesAdded,
    [property: JsonPropertyName("balancesAdded")] int BalancesAdded,
    [property: JsonPropertyName("skipped")] int Skipped);

public sealed record AddonRelease(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record MarketInfo(MarketKey Key, string Name, DateTimeOffset? CapturedAt);

/// <summary>
/// The Blackwater Exchange website's API, as the app uses it. The device key goes in <c>Authorization: Bearer</c>;
/// the app sends no cookies. Every request names the app and its version.
/// </summary>
public sealed class ApiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient http;
    private readonly string version;

    public ApiClient(HttpClient http, string serverUrl, string version)
    {
        this.http = http;
        this.version = version;
        Server = ParseServer(serverUrl);
    }

    public Uri Server { get; }

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    {
        Timeout = RequestTimeout,
        MaxResponseContentBufferSize = 32 * 1024 * 1024,
    };

    public static Uri ParseServer(string? serverUrl)
    {
        if (!Uri.TryCreate(serverUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttps && !(server.Scheme == Uri.UriSchemeHttp && server.IsLoopback))
            || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0)
            throw new ArgumentException("Choose an HTTPS server or loopback HTTP test server.", nameof(serverUrl));
        return server;
    }

    public static bool IsSameOrigin(Uri server, Uri page) => page.IsAbsoluteUri && page.UserInfo.Length == 0
        && server.Scheme == page.Scheme && server.Port == page.Port
        && string.Equals(server.IdnHost, page.IdnHost, StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage Request(HttpMethod method, string path, string? token = null)
    {
        var request = new HttpRequestMessage(method, new Uri(Server, path));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BlackwaterExchangeApp", version));
        request.Headers.Add("X-App-Version", version);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
            return response;
        using (response)
        {
            string? error = null, message = null;
            try
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                error = body.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                message = body.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            }
            catch (JsonException)
            {
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized && error == "unlinked")
                throw new UnlinkedException();
            throw new ApiException(response.StatusCode, error, message ?? response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "The website asked the app to slow down. It will try again later.",
                _ => $"The website answered {(int)response.StatusCode}.",
            });
        }
    }

    // Linking (Accounts/DeviceEndpoints.cs on the server).

    public async Task<LinkStart> StartLinkAsync(string deviceName, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "api/devices/start");
        request.Content = JsonContent.Create(new { name = deviceName, app_version = version });
        using var response = await SendAsync(request, cancellationToken);
        var link = await response.Content.ReadFromJsonAsync<LinkStart>(cancellationToken);
        if (link is null || !IsVerificationUrl(link.VerificationUrl) || !IsVerificationUrl(link.VerificationUrlComplete))
            throw new LinkFailedException("The website returned an invalid verification address.");
        return link;
    }

    private bool IsVerificationUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var page) && IsSameOrigin(Server, page);

    public async Task<LinkPoll> PollLinkAsync(string deviceCode, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "api/devices/token");
        request.Content = JsonContent.Create(new { device_code = deviceCode });
        using var response = await SendAsync(request, cancellationToken, allowNotFound: true);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = body.RootElement;
        var status = root.GetProperty("status").GetString() switch
        {
            "pending" => LinkStatus.Pending,
            "approved" => LinkStatus.Approved,
            "denied" => LinkStatus.Denied,
            "expired" => LinkStatus.Expired,
            "used" => LinkStatus.Used,
            _ => LinkStatus.Unknown,
        };
        return new LinkPoll(status,
            status == LinkStatus.Approved ? root.GetProperty("token").GetString() : null,
            status == LinkStatus.Approved ? root.GetProperty("user").GetProperty("name").GetString() : null);
    }

    /// <summary>
    /// Asks for a link code, hands it to <paramref name="showCode"/> (the window shows it and opens the browser), then
    /// waits for the player to approve it on the website. Returns the device key and account name.
    /// </summary>
    public async Task<(string Token, string UserName)> LinkAsync(string deviceName, Action<LinkStart> showCode,
        CancellationToken cancellationToken)
    {
        var start = await StartLinkAsync(deviceName, cancellationToken);
        showCode(start);
        var until = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresIn);
        var wait = TimeSpan.FromSeconds(Math.Clamp(start.Interval, 2, 30));
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(wait, cancellationToken);
            LinkPoll poll;
            try
            {
                poll = await PollLinkAsync(start.DeviceCode, cancellationToken);
            }
            catch (ApiException exception) when (exception.RateLimited)
            {
                wait += TimeSpan.FromSeconds(5);
                continue;
            }
            switch (poll.Status)
            {
                case LinkStatus.Approved:
                    return (poll.Token!, poll.UserName!);
                case LinkStatus.Pending:
                    continue;
                case LinkStatus.Denied:
                    throw new LinkFailedException("The code was refused on the website.");
                default:
                    throw new LinkFailedException("The code expired. Start linking again.");
            }
        }
        throw new LinkFailedException("The code expired. Start linking again.");
    }

    public async Task<LinkedAccount> MeAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, "api/devices/me", token);
        using var response = await SendAsync(request, cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var device = body.RootElement.GetProperty("device");
        var user = body.RootElement.GetProperty("user");
        return new LinkedAccount(device.GetProperty("id").GetInt64(), device.GetProperty("name").GetString()!,
            user.GetProperty("name").GetString()!, user.GetProperty("share_sales").GetBoolean());
    }

    public async Task UnlinkAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "api/devices/me/unlink", token);
        try
        {
            using var response = await SendAsync(request, cancellationToken);
        }
        catch (UnlinkedException)
        {
            // Already removed on the website.
        }
    }

    // Ledger.

    public async Task<UploadResult> UploadLedgerAsync(string token, byte[] file, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "api/ledger/upload", token);
        request.Content = new ByteArrayContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(request, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<UploadResult>(cancellationToken))!;
    }

    // Prices.

    public async Task<IReadOnlyList<MarketInfo>> MarketsAsync(CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, "v1/markets");
        using var response = await SendAsync(request, cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var markets = new List<MarketInfo>();
        foreach (var market in body.RootElement.EnumerateArray())
        {
            var key = MarketKey.Parse($"{market.GetProperty("region").GetString()}/{market.GetProperty("ruleset_id").GetInt32()}/{market.GetProperty("house_id").GetInt32()}");
            if (key is null)
                continue;
            var captured = market.TryGetProperty("captured_at", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetDateTimeOffset() : (DateTimeOffset?)null;
            markets.Add(new MarketInfo(key, market.GetProperty("name").GetString() ?? key.ToString(), captured));
        }
        return markets;
    }

    /// <summary>One house's prices for the addon (/v1/markets/{market}/addon), or null when there is no fresh scan.</summary>
    public async Task<JsonDocument?> MarketExportAsync(MarketKey market, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"v1/markets/{market}/addon");
        using var response = await SendAsync(request, cancellationToken, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    // Addon releases.

    public async Task<AddonRelease?> LatestAddonAsync(CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, "v1/addon/latest");
        using var response = await SendAsync(request, cancellationToken, allowNotFound: true);
        return response.StatusCode == HttpStatusCode.NotFound ? null
            : await response.Content.ReadFromJsonAsync<AddonRelease>(cancellationToken);
    }

    /// <summary>Downloads a release zip to <paramref name="file"/>, stopping past <paramref name="maxBytes"/>.</summary>
    public async Task DownloadAsync(Uri url, string file, long maxBytes, CancellationToken cancellationToken)
    {
        // Only from this server: a release URL elsewhere is refused.
        if (!IsSameOrigin(Server, url))
            throw new ApiException(HttpStatusCode.Forbidden, "other_server", "The addon download is not on the Blackwater Exchange server.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BlackwaterExchangeApp", version));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, deadline.Token)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new ApiException(HttpStatusCode.RequestEntityTooLarge, "too_large", "The addon download is larger than expected.");
                await target.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The addon download timed out.", exception);
        }
    }
}

public sealed class LinkFailedException(string message) : Exception(message);
