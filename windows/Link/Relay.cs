using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShuangpinLink;

internal sealed record Device(string Id, string Token);
internal sealed record Invitation(string Invite, int ExpiresIn);
internal sealed record Clip(long Revision, string? Envelope);
internal sealed record Snapshot(string Sender, long Version, string Envelope);
internal sealed record Pairing(int V, string Server, string Invite, string Key);
internal sealed record Session(string Server, Device Device, string Key,
    bool Paused = false, bool ShareDictionary = true, long DownloadedDictionary = 0,
    string? UploadedDictionary = null);

internal sealed class Relay
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(27)
    };
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly Uri server;
    readonly string token;

    internal Relay(string serverAddress, string token)
    {
        if (!Uri.TryCreate(serverAddress.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var address)
            || address.Scheme != "https" || address.UserInfo.Length != 0
            || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("服务器地址必须是 HTTPS 地址。");
        server = address;
        this.token = token;
    }

    internal async Task<T?> Send<T>(HttpMethod method, string path, CancellationToken cancel, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(server, "v1/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await Http.SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, cancel);
    }

    internal static string Encode(byte[] value) => Convert.ToBase64String(value);
    internal static string Seal(string key, string kind, byte[] body)
    {
        byte[] result = new byte[12 + body.Length + 16];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var cipher = new AesGcm(Convert.FromBase64String(key), 16);
        cipher.Encrypt(result.AsSpan(0, 12), body, result.AsSpan(12, body.Length),
            result.AsSpan(12 + body.Length), Encoding.UTF8.GetBytes("ime-sync/v1/" + kind));
        return Encode(result);
    }

    internal static byte[] Open(string key, string kind, string envelope, int limit)
    {
        byte[] encoded = Convert.FromBase64String(envelope);
        if (encoded.Length < 28 || encoded.Length > limit + 28)
            throw new InvalidDataException("内容长度错误。");
        byte[] result = new byte[encoded.Length - 28];
        using var cipher = new AesGcm(Convert.FromBase64String(key), 16);
        cipher.Decrypt(encoded.AsSpan(0, 12), encoded.AsSpan(12, result.Length),
            encoded.AsSpan(12 + result.Length), result, Encoding.UTF8.GetBytes("ime-sync/v1/" + kind));
        return result;
    }
}
