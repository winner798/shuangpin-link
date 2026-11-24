using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShuangpinLink;

internal sealed record Device(string Id, string Token);
internal sealed record Invitation(string Invite, int ExpiresIn);
internal sealed record Clip(long Revision, string? Id, string? Sender, long SentAt, string? Envelope);
internal sealed record Snapshot(string Sender, long Version, string Envelope);
internal sealed record Pairing(int V, string Server, string Invite, string Key);
internal sealed record Session(string Server, Device Device, string Key,
    bool Paused = false, bool ShareDictionary = true, long DownloadedDictionary = 0,
    string? UploadedDictionary = null, long AcceptedClipboardTime = 0, bool ImagesEnabled = false, bool PreserveImageMetadata = false);

internal sealed class Relay
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(27)
    };
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // HTTP request bodies are standalone JSON. Escaping '+' in an encrypted Base64
    // block adds about 8% to its wire size and exceeds the relay's bounded envelope.
    static readonly JsonSerializerOptions RequestJson = new(Json) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
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

    internal async Task<T?> Send<T>(HttpMethod method, string path, CancellationToken cancel, object? body = null,
        string protocol = "v1")
    {
        using var request = CreateRequest(method, path, protocol);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, RequestJson));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var deadline = protocol is "v2" or "v3" ? CancellationTokenSource.CreateLinkedTokenSource(cancel) : null;
        deadline?.CancelAfter(TimeSpan.FromSeconds(27));
        var token = deadline?.Token ?? cancel;
        using var response = await Http.SendAsync(request,
            deadline is null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        response.EnsureSuccessStatusCode();
        if (deadline is not null)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            int maxBytes = protocol == "v3" ? 350 * 1024 : 88 * 1024;
            if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
                throw new InvalidDataException("中继返回的剪贴板响应过大。");
            var capacity = response.Content.Headers.ContentLength.HasValue
                ? (int)response.Content.Headers.ContentLength.Value : maxBytes;
            var bytes = new byte[capacity + 1];
            int length = await stream.ReadAtLeastAsync(bytes.AsMemory(), bytes.Length, false, token);
            if (length == bytes.Length) throw new InvalidDataException("中继返回的剪贴板响应过大。");
            return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length), Json);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, cancel);
    }

    internal async Task<HttpResponseMessage> GetStream(string path, CancellationToken cancel)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
    }

    HttpRequestMessage CreateRequest(HttpMethod method, string path, string protocol = "v1")
    {
        var request = new HttpRequestMessage(method, new Uri(server, protocol + "/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    internal static string Encode(byte[] value) => Convert.ToBase64String(value);
    internal static string Seal(string key, string kind, byte[] body)
    {
        byte[] result = new byte[12 + body.Length + 16];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var cipher = CreateCipher(key);
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
        try
        {
            using var cipher = CreateCipher(key);
            cipher.Decrypt(encoded.AsSpan(0, 12), encoded.AsSpan(12, result.Length),
                encoded.AsSpan(12 + result.Length), result, Encoding.UTF8.GetBytes("ime-sync/v1/" + kind));
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }

    static AesGcm CreateCipher(string key)
    {
        byte[] bytes = Convert.FromBase64String(key);
        try
        {
            if (bytes.Length != 32) throw new CryptographicException("设备加密密钥无效。");
            return new AesGcm(bytes, 16);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
