using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ShuangpinLink;

internal sealed partial class LinkWindow
{
    static readonly UTF8Encoding ClipEncoding = new(false, true);
    readonly SemaphoreSlim clipboardChanged = new(0, 1);
    CancellationTokenSource? clipboardLink;
    Task clipboardWorkers = Task.CompletedTask;
    OutgoingClip? clipboardPending;
    uint lastClipboardSequence;
    uint syncBaseline;
    long lastClipboardSentAt;
    bool clipboardPeerReady;
    bool changingClipboardPair;
    sealed record OutgoingClip(string Id, long SentAt, string Envelope, uint Sequence);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        lastClipboardSequence = PrivateClipboard.Sequence;
        WTSRegisterSessionNotification(Handle, 0);
        AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        WTSUnRegisterSessionNotification(Handle);
        RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x02B1)
        {
            if (message.WParam == 7) // WTS_SESSION_LOCK
            {
                sessionLocked = true;
                historyWindow?.Close();
                StopClipboard();
            }
            else if (message.WParam == 8)
            {
                sessionLocked = false;
                lastClipboardSequence = PrivateClipboard.Sequence;
                RestartClipboard();
            }
        }
        if (message.Msg == 0x031D) CaptureClipboard();
        base.WndProc(ref message);
    }

    void RestartClipboard()
    {
        StopClipboard();
        syncBaseline = PrivateClipboard.Sequence;
        if (sessionLocked || session is not { Paused: false } device || changingClipboardPair || IsDisposed || exiting) return;
        var relay = new Relay(device.Server, device.Device.Token);
        clipboardLink = new CancellationTokenSource();
        var token = clipboardLink.Token;
        clipboardWorkers = Task.WhenAll(ReceiveClipboard(relay, device.Key, token), UploadClipboard(relay, token),
            MonitorImages(relay, device, token));
    }

    void StopClipboard()
    {
        if (clipboardLink is { } source)
        {
            source.Cancel();
            if (session is { ImagesEnabled: true } device && (device.Paused || changingClipboardPair || sessionLocked || exiting))
                _ = UnregisterImages(device);
            _ = FinishClipboardWorkers(source, clipboardWorkers);
            clipboardLink = null;
        }
        clipboardPending = null;
        clipboardPeerReady = false;
        StopImageOperations();
        if (sessionLocked || changingClipboardPair || exiting || session is not { ImagesEnabled: true, Paused: false }) PruneImageCache(clear: true);
    }

    static async Task FinishClipboardWorkers(CancellationTokenSource source, Task workers)
    {
        try { await workers; }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) when (source.IsCancellationRequested) { }
        finally { source.Dispose(); }
    }

    void CaptureClipboard()
    {
        uint sequence = PrivateClipboard.Sequence;
        if (sequence == lastClipboardSequence) return;
        lastClipboardSequence = sequence;
        clipboardPending = null;
        NoteLocalClipboardChange();
        bool send = clipboardPeerReady && clipboardLink is { IsCancellationRequested: false } && sequence != syncBaseline;
        if ((!CanRecordHistory && !send) || !InteractiveDesktop()) return;
        try
        {
            var clipboard = Clipboard.GetDataObject();
            if (clipboard is null || PrivateClipboard.IsProtected(clipboard)) return;
            if (send && session is { ImagesEnabled: true } && CaptureNewImage(sequence)) return;
            if (clipboard.GetData(DataFormats.UnicodeText, autoConvert: false) is not string { Length: > 0 } text
                || text.Contains('\0') || ClipEncoding.GetByteCount(text) > ClipboardWire.TextLimit
                || sequence != PrivateClipboard.Sequence) return;

            _ = AddHistory(text);
            if (!send || session is not { Paused: false } device) return;
            long sentAt = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lastClipboardSentAt + 1);
            string id = Guid.NewGuid().ToString("N");
            byte[] bytes = ClipEncoding.GetBytes(text);
            try
            {
                clipboardPending = new OutgoingClip(id, sentAt,
                    ClipboardWire.Seal(device.Key, ClipboardWire.Owner, id, sentAt, bytes), sequence);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            lastClipboardSentAt = sentAt;
            if (clipboardChanged.CurrentCount == 0) clipboardChanged.Release();
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or CryptographicException or FormatException)
        { }
    }

    async Task UploadClipboard(Relay relay, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (clipboardPending is not { } outgoing)
            { await clipboardChanged.WaitAsync(token); continue; }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!clipboardPeerReady || outgoing.Sequence != PrivateClipboard.Sequence
                || outgoing.SentAt <= now - ClipboardWire.Lifetime || outgoing.SentAt > now + ClipboardWire.AllowedClockSkew)
            { clipboardPending = null; continue; }
            try
            {
                token.ThrowIfCancellationRequested();
                await relay.Send<object>(HttpMethod.Put, "clip", token,
                    new { outgoing.Id, outgoing.SentAt, outgoing.Envelope }, protocol: "v2");
                token.ThrowIfCancellationRequested();
                if (clipboardPending == outgoing) clipboardPending = null;
            }
            catch (HttpRequestException error) when (!token.IsCancellationRequested
                && error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Conflict)
            {
                if (clipboardPending == outgoing) clipboardPending = null;
                if (error.StatusCode != HttpStatusCode.BadRequest) clipboardPeerReady = false;
            }
            catch (Exception error) when (Retryable(error, token)) { await Task.Delay(1500, token); }
        }
    }

    async Task ReceiveClipboard(Relay relay, string key, CancellationToken token)
    {
        long after = 0;
        while (!token.IsCancellationRequested)
        {
            long localChange = localClipboardGeneration;
            try
            {
                int wait = clipboardPeerReady ? 20 : 0;
                var clip = await relay.Send<Clip>(HttpMethod.Get, $"clip?after={after}&wait={wait}", token, protocol: "v2");
                token.ThrowIfCancellationRequested();
                if (!clipboardPeerReady)
                {
                    // A copy made before the relay confirms both devices is a local-only copy.
                    syncBaseline = PrivateClipboard.Sequence;
                    clipboardPeerReady = true;
                }
                connectionStatus.Text = "已配对 · 加密剪贴板在线";
                if (clip is null) continue;
                if (clip.Revision <= after) throw new InvalidDataException("中继返回的剪贴板版本无效。");
                after = clip.Revision;
                CaptureClipboard();
                if (clip.Envelope is null || session == null || clip.SentAt <= session.AcceptedClipboardTime) continue;
                if (session.ImagesEnabled ? clip.SentAt <= lastLocalClipboardAt
                    : clipboardPending != null || localChange != localClipboardGeneration) continue;
                ApplyAuthenticatedClipboard(key, clip, PrivateClipboard.Sequence, token);
            }
            catch (Exception error) when (Retryable(error, token))
            {
                bool unpaired = error is HttpRequestException { StatusCode: HttpStatusCode.Conflict };
                bool unauthorized = error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };
                if (unpaired || unauthorized)
                {
                    clipboardPending = null;
                    clipboardPeerReady = false;
                }
                connectionStatus.Text = unpaired ? "等待手机配对" : unauthorized ? "设备已解绑，请重新配对。"
                    : "剪贴板暂不可用，正在重试。";
                await Task.Delay(unpaired ? 5000 : 1500, token);
            }
        }
    }

    void ApplyAuthenticatedClipboard(string key, Clip clip, uint sequence, CancellationToken token)
    {
        if (session is not { Paused: false } active || clip is not { Id: { } id, Sender: { } sender, Envelope: { } envelope })
            return;
        byte[] plain = ClipboardWire.Open(key, sender, ClipboardWire.Peer, id, clip.SentAt, envelope,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), active.AcceptedClipboardTime);
        try
        {
            string text = ClipEncoding.GetString(plain);
            if (text.Contains('\0')) throw new InvalidDataException("剪贴板文字格式无效。");
            token.ThrowIfCancellationRequested();
            if (sequence != PrivateClipboard.Sequence || !InteractiveDesktop()) return;
            session = active with { AcceptedClipboardTime = clip.SentAt };
            SaveSession();
            if (PrivateClipboard.Write(Handle, sequence, text))
            {
                lastClipboardSequence = PrivateClipboard.Sequence;
                clipboardPending = null;
                sendingImage?.Cancel();
                receivingImage?.Cancel();
                _ = AddHistory(text);
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(nint window);
}
