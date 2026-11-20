using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShuangpinLink;

internal sealed record UpdateVersion(int VersionCode, string VersionName);
internal sealed record WindowsRelease(UpdateVersion Version, long Size, byte[] Sha256);

internal static class WindowsUpdate
{
    const int MaxManifestBytes = 16 * 1024;
    const long MaxInstallerBytes = 300L * 1024 * 1024;
    const int MaxRedirects = 5;
    const string Platform = "windows-x64";
    const string InstallerName = "ShuangpinLinkSetup.exe";
    const string ReleasesBase = "https://github.com/winner798/shuangpin-link/releases/";
    static readonly Regex VersionName = new(@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z");
    static readonly HashSet<string> ReleaseHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"
    };
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
    }) { Timeout = Timeout.InfiniteTimeSpan };
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShuangpinLink", "updates");

    sealed record Envelope(string Manifest, string Signature);
    sealed record Artifact(long Size, string Sha256);
    sealed record Manifest(int VersionCode, string VersionName, Dictionary<string, Artifact> Files);

    internal static UpdateVersion Current { get; } = ReadVersion();

    static UpdateVersion ReadVersion()
    {
        using var stream = Resource("ShuangpinLink.UpdateVersion.json");
        return JsonSerializer.Deserialize<UpdateVersion>(stream, Json)
            ?? throw new InvalidDataException("缺少当前版本信息。");
    }

    static Stream Resource(string name) => typeof(WindowsUpdate).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException("缺少更新配置。");

    static async Task<HttpResponseMessage> OpenReleaseFile(string path, CancellationToken cancel)
    {
        Uri address = new Uri(ReleasesBase + path);
        for (int redirects = 0; ; redirects++)
        {
            if (address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0
                || !address.IsDefaultPort || address.Fragment.Length != 0 || !ReleaseHosts.Contains(address.IdnHost))
                throw new InvalidDataException("更新下载地址无效。");

            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            request.Headers.AcceptEncoding.ParseAdd("identity");
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;

            using (response)
            {
                if (redirects == MaxRedirects || response.Headers.Location is not { } location
                    || !Uri.TryCreate(address, location, out var next))
                    throw new InvalidDataException("更新下载重定向无效。");
                address = next;
            }
        }
    }

    internal static async Task<WindowsRelease?> Check(CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await OpenReleaseFile("latest/download/update-manifest.json", deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxManifestBytes)
            throw new InvalidDataException("更新信息过大。");

        using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[MaxManifestBytes + 1];
        int length = await body.ReadAtLeastAsync(buffer, buffer.Length, false, deadline.Token);
        if (length > MaxManifestBytes) throw new InvalidDataException("更新信息过大。");
        return VerifyManifest(buffer.AsSpan(0, length));
    }

    static WindowsRelease? VerifyManifest(ReadOnlySpan<byte> contents)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(contents, Json);
        if (envelope is not { Manifest.Length: > 0, Signature.Length: > 0 })
            throw new InvalidDataException("缺少更新签名。");
        var rawManifest = Convert.FromBase64String(envelope.Manifest);
        using var key = new StreamReader(Resource("ShuangpinLink.UpdatePublicKey.txt"));
        var keyBytes = Convert.FromBase64String(key.ReadToEnd());
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(keyBytes, out int bytesRead);
        if (bytesRead != keyBytes.Length || !rsa.VerifyData(rawManifest, Convert.FromBase64String(envelope.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("更新签名校验失败。");

        var manifest = JsonSerializer.Deserialize<Manifest>(rawManifest, Json);
        if (manifest is not { VersionCode: > 0 and <= 2_100_000_000, VersionName.Length: > 0 and <= 40 }
            || !VersionName.IsMatch(manifest.VersionName))
            throw new InvalidDataException("更新版本信息无效。");
        if (manifest.VersionCode <= Current.VersionCode) return null;

        if (manifest.Files is null || !manifest.Files.TryGetValue(Platform, out var file)
            || file is not { Size: > 0 and <= MaxInstallerBytes, Sha256.Length: 64 }
            || file.Sha256.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("更新包信息无效。");
        return new WindowsRelease(new UpdateVersion(manifest.VersionCode, manifest.VersionName),
            file.Size, Convert.FromHexString(file.Sha256));
    }

    internal static async Task<string> Download(WindowsRelease release, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var token = deadline.Token;
        string version = release.Version.VersionName;
        if (version.Length > 40 || !VersionName.IsMatch(version))
            throw new InvalidDataException("更新版本信息无效。");
        using var response = await OpenReleaseFile($"download/v{version}/{InstallerName}", token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("安装包长度与发布信息不一致。");

        string directory = Directory.CreateDirectory(Path.Combine(CacheDirectory, Guid.NewGuid().ToString("N"))).FullName;
        string installer = Path.Combine(directory, InstallerName);
        string partial = installer + ".partial";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var body = await response.Content.ReadAsStreamAsync(token);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                int size;
                while ((size = await body.ReadAsync(buffer, token)) > 0)
                {
                    received += size;
                    if (received > release.Size) throw new InvalidDataException("安装包长度超出发布信息。");
                    hash.AppendData(buffer, 0, size);
                    await file.WriteAsync(buffer.AsMemory(0, size), token);
                }
                if (received != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), release.Sha256))
                    throw new InvalidDataException("安装包校验失败。");
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, installer);
            return installer;
        }
        catch
        {
            RemoveDownload(installer);
            throw;
        }
    }

    internal static void RemoveDownload(string installer)
    {
        string? directory = Path.GetDirectoryName(installer);
        if (directory is null || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
            || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(directory), CacheDirectory)) return;

        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            File.Delete(installer + ".partial");
            File.Delete(installer);
            Directory.Delete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void CleanPreviousDownloads()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory) || (File.GetAttributes(CacheDirectory) & FileAttributes.ReparsePoint) != 0)
                return;
            foreach (string directory in Directory.EnumerateDirectories(CacheDirectory))
                RemoveDownload(Path.Combine(directory, InstallerName));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
