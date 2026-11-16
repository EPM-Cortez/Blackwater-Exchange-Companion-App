using System.Net;
using System.Text;

namespace BlackwaterExchange.Tests;

/// <summary>Answers the app's requests in tests, and records them.</summary>
public sealed class FakeServer(Func<HttpRequestMessage, byte[]?, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, byte[]? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add((request, body));
        return answer(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public static ApiClient Client(FakeServer server) => new(new HttpClient(server), "https://blackwater.test", "0.1.0");
}

/// <summary>A throwaway folder, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("blackwater-test-").FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
