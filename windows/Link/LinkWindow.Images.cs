using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShuangpinLink;

internal sealed partial class LinkWindow
{
    readonly CheckBox syncImages = new GlassCheckBox { AutoSize = true, Text = "自动收发图片" };
    readonly CheckBox keepImageMetadata = new GlassCheckBox { AutoSize = true, Text = "发送时保留原始元数据" };
    readonly Label imageStatus = Copy("图片互传已关闭");
    readonly GlassButton cancelImage = new() { Text = "取消传输", Enabled = false };
    readonly GlassButton retryImage = new() { Text = "重试发送", Enabled = false };
    static string ImageCache => Path.Combine(DataDirectory, "paste-images");
    CancellationTokenSource? sendingImage;
    CancellationTokenSource? receivingImage;
    readonly SemaphoreSlim capabilityGate = new(1, 1);
    uint? retrySequence;
    long localClipboardGeneration;
    long lastLocalClipboardAt;
    bool peerAcceptsImages;

    sealed record Capability(bool PeerEnabled);
    sealed record ClipImage(long Revision, string Id, string Sender, long SentAt, int Size);
    sealed record ImageChunk(string Envelope);

    long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    void ConfigureImages()
    {
        syncImages.CheckedChanged += (_, _) => ChangeImageSettings();
        keepImageMetadata.CheckedChanged += (_, _) => ChangeImageSettings();
        cancelImage.Click += (_, _) =>
        {
            if (sendingImage != null)
            {
                retrySequence = PrivateClipboard.Sequence;
                retryImage.Enabled = true;
            }
            sendingImage?.Cancel();
            receivingImage?.Cancel();
            imageStatus.Text = "图片传输已取消";
        };
        retryImage.Click += (_, _) =>
        {
            if (retrySequence is uint sequence && sequence == PrivateClipboard.Sequence && InteractiveDesktop()
                && session is { Paused: false, ImagesEnabled: true } && !sessionLocked)
                CaptureNewImage(sequence);
        };
        historyExpiry.Tick += (_, _) => PruneImageCache();
        PruneImageCache();
    }

    void ChangeImageSettings()
    {
        if (updating || session == null) return;
        session = session with { ImagesEnabled = syncImages.Checked, PreserveImageMetadata = keepImageMetadata.Checked };
        try { SaveSession(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { status.Text = "图片设置已在本次运行生效，但暂时无法保存。"; }
        RestartClipboard();
        UpdateControls();
    }

    void NoteLocalClipboardChange()
    {
        localClipboardGeneration++;
        lastLocalClipboardAt = NowMillis();
        sendingImage?.Cancel();
        receivingImage?.Cancel();
        retrySequence = null;
        retryImage.Enabled = false;
    }

    void StopImageOperations()
    {
        sendingImage?.Cancel();
        receivingImage?.Cancel();
        peerAcceptsImages = false;
        retrySequence = null;
        cancelImage.Enabled = retryImage.Enabled = false;
        imageStatus.Text = session is not { ImagesEnabled: true } ? "图片互传已关闭" :
            (session.Paused || sessionLocked ? "图片互传已暂停" : "等待手机开启图片互传");
    }

    async Task UnregisterImages(Session device)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RegisterImages(new Relay(device.Server, device.Device.Token), false, timeout.Token);
        }
        catch (Exception error) when (ImageError(error)) { }
    }

    async Task<bool> RegisterImages(Relay relay, bool enabled, CancellationToken token)
    {
        await capabilityGate.WaitAsync(token);
        try
        {
            var result = await relay.Send<Capability>(HttpMethod.Put, "image/capability?enabled=" + (enabled ? "1" : "0"), token, protocol: "v3");
            return result?.PeerEnabled == true;
        }
        finally { capabilityGate.Release(); }
    }

    async Task MonitorImages(Relay relay, Session device, CancellationToken token)
    {
        long after = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool ready = await RegisterImages(relay, device.ImagesEnabled, token);
                token.ThrowIfCancellationRequested();
                if (!device.ImagesEnabled) return;
                if (ready != peerAcceptsImages && sendingImage == null && receivingImage == null)
                    imageStatus.Text = ready ? "图片互传已就绪" : "等待手机开启图片互传";
                peerAcceptsImages = ready;
                if (!ready) { await Task.Delay(5000, token); continue; }
                var image = await relay.Send<ClipImage>(HttpMethod.Get, $"image?after={after}&wait=15", token, protocol: "v3");
                token.ThrowIfCancellationRequested();
                if (image == null) continue;
                if (image.Revision <= after) throw new InvalidDataException("图片事件顺序无效。");
                CaptureClipboard();
                if (AcceptImage(image) && InteractiveDesktop())
                {
                    using var download = CancellationTokenSource.CreateLinkedTokenSource(token);
                    receivingImage = download;
                    cancelImage.Enabled = true;
                    try { await DownloadImage(relay, device.Key, image, PrivateClipboard.Sequence, download.Token); }
                    catch (OperationCanceledException) when (download.IsCancellationRequested && !token.IsCancellationRequested) { }
                    catch (Exception error) when (error is CryptographicException or InvalidDataException or FormatException)
                    { if (!download.IsCancellationRequested) imageStatus.Text = "图片校验失败，已跳过"; }
                    catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    { if (!download.IsCancellationRequested) imageStatus.Text = "图片已取消或过期"; }
                    finally
                    {
                        if (ReferenceEquals(receivingImage, download)) receivingImage = null;
                        cancelImage.Enabled = sendingImage != null || receivingImage != null;
                    }
                }
                after = image.Revision;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (Retryable(error, token) || error is InvalidDataException)
            {
                if (token.IsCancellationRequested || !device.ImagesEnabled) return;
                peerAcceptsImages = false;
                bool legacy = error is HttpRequestException { StatusCode: HttpStatusCode.NotFound };
                imageStatus.Text = legacy ? "服务器需升级才能收发图片" : "图片通道暂不可用，正在重试";
                await Task.Delay(legacy ? 60000 : 3500, token);
            }
        }
    }

    bool AcceptImage(ClipImage image)
    {
        long now = NowMillis();
        return session is { Paused: false, ImagesEnabled: true } active && !sessionLocked && image.Sender == ClipboardWire.Peer
            && image.Id is { Length: 32 } && ClipboardWire.ValidId(image.Id) && image.Size is > 0 and <= ImageWire.MaxSize
            && image.SentAt > Math.Max(active.AcceptedClipboardTime, lastLocalClipboardAt)
            && image.SentAt > now - ClipboardWire.Lifetime && image.SentAt <= now + ClipboardWire.AllowedClockSkew;
    }

    bool CaptureNewImage(uint sequence)
    {
        if (clipboardLink == null || session is not { Paused: false, ImagesEnabled: true } active) return false;
        try
        {
            var source = ClipboardImages.Capture(Handle, sequence, probeOnly: true);
            if (source == null) return false;
            sendingImage?.Cancel();
            retrySequence = null;
            retryImage.Enabled = false;
            if (!peerAcceptsImages)
            {
                retrySequence = sequence;
                retryImage.Enabled = true;
                imageStatus.Text = "等待手机和服务器就绪后可重试";
                return true;
            }
            long timestamp = Math.Max(NowMillis(), lastClipboardSentAt + 1);
            lastClipboardSentAt = timestamp;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(clipboardLink.Token);
            sendingImage = cancel;
            cancelImage.Enabled = true;
            _ = UploadImage(new Relay(active.Server, active.Device.Token), active, sequence, timestamp, cancel);
        }
        catch (Exception error) when (error is InvalidDataException or ExternalException or IOException or UnauthorizedAccessException)
        { imageStatus.Text = error is InvalidDataException ? error.Message : "无法读取图片，请重新复制"; }
        return true;
    }

    async Task UploadImage(Relay relay, Session device, uint sequence, long timestamp, CancellationTokenSource cancel)
    {
        string id = Guid.NewGuid().ToString("N");
        byte[]? plain = null;
        ClipboardImageSource? source = null;
        var token = cancel.Token;
        bool created = false, completed = false;
        try
        {
            imageStatus.Text = "正在准备图片";
            source = await Task.Run(() => ClipboardImages.Capture(0, sequence), token);
            if (source == null) return;
            plain = await Task.Run(() => source.ReadImage(device.PreserveImageMetadata), token);
            token.ThrowIfCancellationRequested();
            if (PrivateClipboard.Sequence != sequence) return;
            int count = ImageWire.Count(plain.Length);
            await relay.Send<object>(HttpMethod.Put, FormattableString.Invariant($"image/{id}?sentAt={timestamp}&size={plain.Length}"), token, protocol: "v3");
            created = true;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (PrivateClipboard.Sequence != sequence) return;
                imageStatus.Text = $"正在发送图片 · {i * 100 / count}%";
                var packet = await Task.Run(() => new ImageChunk(ImageWire.Seal(device.Key, ClipboardWire.Owner, id, timestamp, plain.Length, i, plain)), token);
                // Retry a lost acknowledgement using the identical authenticated block.
                for (int attempt = 1; ; attempt++)
                {
                    try { await relay.Send<object>(HttpMethod.Put, $"image/{id}/{i}", token, packet, "v3"); break; }
                    catch (Exception error) when (attempt < 3 && !token.IsCancellationRequested &&
                        (error is OperationCanceledException or IOException or HttpRequestException { StatusCode: null }))
                    { await Task.Delay(500 * attempt, token); }
                }
            }
            completed = true;
            if (!token.IsCancellationRequested) imageStatus.Text = "图片已发送";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (ImageError(error))
        {
            if (!token.IsCancellationRequested && PrivateClipboard.Sequence == sequence)
            {
                retrySequence = sequence;
                retryImage.Enabled = true;
                imageStatus.Text = error is InvalidDataException ? error.Message : "图片发送失败，可重试";
            }
        }
        finally
        {
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            if (source?.Bytes != null && !ReferenceEquals(source.Bytes, plain)) CryptographicOperations.ZeroMemory(source.Bytes);
            if (ReferenceEquals(sendingImage, cancel)) { sendingImage = null; cancelImage.Enabled = receivingImage != null; }
            cancel.Dispose();
            if (created && !completed)
            {
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await relay.Send<object>(HttpMethod.Delete, $"image/{id}", cleanup.Token, protocol: "v3"); }
                catch (Exception error) when (ImageError(error)) { }
            }
        }
    }

    static bool ImageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or CryptographicException or OperationCanceledException or HttpRequestException or JsonException or ArgumentException
        or ExternalException or FormatException or OutOfMemoryException;

    async Task DownloadImage(Relay relay, string key, ClipImage image, uint sequence, CancellationToken token)
    {
        var bytes = new byte[image.Size];
        byte[]? dib = null;
        string? path = null;
        try
        {
            int count = ImageWire.Count(image.Size);
            for (int i = 0; i < count; i++)
            {
                if (!AcceptImage(image) || PrivateClipboard.Sequence != sequence) return;
                imageStatus.Text = $"正在接收图片 · {i * 100 / count}%";
                var chunk = await relay.Send<ImageChunk>(HttpMethod.Get, $"image/{image.Id}/{i}", token, protocol: "v3");
                if (chunk?.Envelope == null) throw new InvalidDataException("图片缺少分块。");
                await Task.Run(() => ImageWire.Open(key, image.Sender, image.Id, image.SentAt, image.Size, i, chunk.Envelope, bytes), token);
            }
            var info = await Task.Run(() => ImageFormats.Inspect(bytes), token);
            dib = await Task.Run(() => ClipboardImages.PasteDib(bytes, info), token);
            token.ThrowIfCancellationRequested();
            if (!AcceptImage(image) || sequence != PrivateClipboard.Sequence || !InteractiveDesktop()) return;
            Directory.CreateDirectory(ImageCache);
            string ext = info.Mime switch { "image/jpeg" => "jpg", "image/png" => "png", "image/webp" => "webp",
                "image/gif" => "gif", _ => throw new InvalidDataException("图片格式不支持。") };
            path = Path.Combine(ImageCache, Guid.NewGuid().ToString("N") + "." + ext);
            await File.WriteAllBytesAsync(path, bytes, token);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            token.ThrowIfCancellationRequested();
            if (!AcceptImage(image) || sequence != PrivateClipboard.Sequence || !InteractiveDesktop()) return;
            if (ClipboardImages.Write(Handle, sequence, path, bytes, info.Mime, dib))
            {
                path = null; // clipboard file paste stays available until the cache expires
                lastClipboardSequence = PrivateClipboard.Sequence;
                clipboardPending = null;
                sendingImage?.Cancel();
                session = session! with { AcceptedClipboardTime = image.SentAt };
                SaveSession();
                imageStatus.Text = $"已收到图片 · {info.Width} × {info.Height}";
                PruneImageCache();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (dib != null) CryptographicOperations.ZeroMemory(dib);
            if (path != null) DeleteCachedImage(path);
        }
    }

    static void PruneImageCache(bool clear = false)
    {
        try
        {
            if (!Directory.Exists(ImageCache)) return;
            var files = new DirectoryInfo(ImageCache).EnumerateFiles()
                .Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "N", out _))
                .OrderByDescending(f => f.LastWriteTimeUtc);
            int kept = 0;
            foreach (var file in files)
                if (clear || ++kept > 4 || DateTime.UtcNow - file.LastWriteTimeUtc >= TimeSpan.FromMinutes(10)) DeleteCachedImage(file.FullName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    static void DeleteCachedImage(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            // All regular files here are private paste copies created by this process.
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
