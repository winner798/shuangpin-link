using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using QRCoder;

namespace ShuangpinLink;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var instance = new Mutex(true, @"Local\ShuangpinLink", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new LinkWindow(args.Contains("--tray")));
    }
}

internal sealed class LinkWindow : Form
{
    static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShuangpinLink", "state");
    static readonly string StateFile = Path.Combine(DataDirectory, "device.bin");
    static readonly UTF8Encoding Utf8 = new(false, true);
    const int ClipLimit = 64 * 1024;
    const int DictionaryLimit = 8 * 1024 * 1024;
    readonly TextBox server = new() { Width = 370, Text = "https://1.15.223.20/ime-sync/" };
    readonly TextBox setupCode = new() { Width = 370, UseSystemPasswordChar = true, PlaceholderText = "服务器启用码" };
    readonly PictureBox qr = new() { Width = 224, Height = 224, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(420, 0), Text = "尚未绑定服务器" };
    readonly CheckBox pause = new() { AutoSize = true, Text = "暂停剪贴板同步" };
    readonly CheckBox share = new() { AutoSize = true, Text = "共享个人词库", Checked = true };
    readonly CheckBox learning = new() { AutoSize = true, Text = "学习选词习惯", Checked = true };
    readonly Button checkUpdate = new() { Text = "检查更新", AutoSize = true };
    readonly Button cancelUpdate = new() { Text = "取消更新", AutoSize = true, Visible = false };
    readonly Label updateStatus = new() { AutoSize = true, MaximumSize = new Size(400, 0), Text = $"当前版本 {WindowsUpdate.Current.VersionName}" };
    readonly NotifyIcon tray = new() { Text = "双拼互联", Icon = SystemIcons.Application, Visible = true };
    readonly System.Windows.Forms.Timer qrExpiry = new() { Interval = 5 * 60 * 1000 };
    readonly bool startHidden;
    Session? session;
    CancellationTokenSource? link;
    CancellationTokenSource? updateRequest;
    Task[] workers = [];
    bool updating;
    bool exiting;
    bool busy;
    bool applying;
    uint appliedSequence;
    long revision;
    Pending? pending;
    readonly SemaphoreSlim pendingChanged = new(0, 1);
    readonly SemaphoreSlim dictionaryGate = new(1, 1);
    sealed record Pending(string Id, string Text, uint Sequence, DateTimeOffset Created);

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    internal LinkWindow(bool startHidden)
    {
        this.startHidden = startHidden;
        Text = "双拼互联";
        ClientSize = new Size(450, 545);
        MinimumSize = new Size(470, 500);
        Font = new Font("Microsoft YaHei UI", 10f);
        BackColor = SystemColors.Window;
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(18) };
        var actions = new FlowLayoutPanel { AutoSize = true };
        var dictionaryActions = new FlowLayoutPanel { AutoSize = true };
        var updateActions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.AddRange([Button("启用或恢复", Connect), Button("显示配对码", Invite), Button("解除手机配对", ResetPeer)]);
        dictionaryActions.Controls.AddRange([Button("立即同步词库", SyncNow), Button("清空本机词库", ClearDictionary)]);
        updateActions.Controls.AddRange([checkUpdate, cancelUpdate]);
        checkUpdate.Click += async (_, _) => await CheckForUpdates();
        cancelUpdate.Click += (_, _) => updateRequest?.Cancel();
        content.Controls.AddRange([new Label { Text = "双拼互联", Font = new Font(Font.FontFamily, 19f, FontStyle.Bold), AutoSize = true },
            status, new Label { Text = "同步服务器", AutoSize = true }, server, setupCode, actions, pause, share, learning,
            dictionaryActions, updateActions, updateStatus, qr]);
        Controls.Add(content);

        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("打开设置", null, (_, _) => Reveal());
        tray.ContextMenuStrip.Items.Add("检查更新", null, async (_, _) => { Reveal(); await CheckForUpdates(); });
        tray.ContextMenuStrip.Items.Add("暂停或恢复同步", null, (_, _) => pause.Checked = !pause.Checked);
        tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray.DoubleClick += (_, _) => Reveal();
        qrExpiry.Tick += (_, _) => ClearQr();

        try { session = LoadSession(); }
        catch (Exception error) when (error is IOException or CryptographicException or JsonException)
        { status.Text = "设备设置无法读取，请用服务器启用码恢复。"; }
        UpdateControls();
        pause.CheckedChanged += async (_, _) => await RunAction(async () =>
        {
            if (updating || session is null) return;
            session = session with { Paused = pause.Checked };
            SaveSession();
            pending = null;
            await Restart();
        });
        share.CheckedChanged += async (_, _) => await RunAction(async () =>
        {
            if (updating || session is null) return;
            session = session with { ShareDictionary = share.Checked };
            SaveSession();
            await Restart();
        });
        learning.CheckedChanged += async (_, _) => await RunAction(async () =>
        {
            if (updating) return;
            await dictionaryGate.WaitAsync();
            try { await RimeFiles.SetLearning(learning.Checked, CancellationToken.None); }
            finally { dictionaryGate.Release(); }
        });
    }

    Button Button(string label, Func<Task> action)
    {
        var button = new Button { Text = label, AutoSize = true };
        button.Click += async (_, _) => await RunAction(action);
        return button;
    }

    async Task RunAction(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        try { await action(); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        { status.Text = "凭据无效或已撤销。"; }
        catch (HttpRequestException) { status.Text = "连接失败，请检查服务器地址或配对状态。"; }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or CryptographicException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { status.Text = "操作失败。请检查输入法安装和设备设置。"; }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException) { status.Text = "操作超时，请稍后重试。"; }
        finally { busy = false; UpdateControls(); }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startHidden) Hide();
        WindowsUpdate.CleanPreviousDownloads();
        await Restart();
    }

    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        { e.Cancel = true; Hide(); return; }
        link?.Cancel();
        updateRequest?.Cancel();
        ClearQr();
        tray.Dispose();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x031D) ReadClipboard();
        base.WndProc(ref message);
    }

    void ReadClipboard()
    {
        uint sequence = GetClipboardSequenceNumber();
        if (applying || sequence == appliedSequence || session is not { Paused: false }) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)
                || data.GetDataPresent("CanUploadToCloudClipboard", false)
                || data.GetDataPresent("CanIncludeInClipboardHistory", false))
            { pending = null; return; }
            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (text.Length == 0 || Utf8.GetByteCount(text) > ClipLimit)
            { pending = null; return; }
            pending = new Pending(Guid.NewGuid().ToString("N"), text, sequence, DateTimeOffset.UtcNow);
            if (pendingChanged.CurrentCount == 0) pendingChanged.Release();
        }
        catch (Exception error) when (error is ExternalException or EncoderFallbackException) { }
    }

    async Task Restart()
    {
        link?.Cancel();
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) { }
        link?.Dispose();
        workers = [];
        if (session is not { } device) return;
        link = new CancellationTokenSource();
        var relay = new Relay(device.Server, device.Device.Token);
        CancellationToken token = link.Token;
        List<Task> work = [];
        if (!device.Paused)
        {
            work.Add(Receive(relay, device.Key, token));
            work.Add(Upload(relay, device.Key, token));
        }
        if (device.ShareDictionary) work.Add(DictionaryLoop(token));
        workers = [..work];
    }

    async Task Receive(Relay relay, string key, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            uint started = GetClipboardSequenceNumber();
            try
            {
                var clip = await relay.Send<Clip>(HttpMethod.Get, $"clip?after={revision}&wait=20", token);
                if (clip is null) { status.Text = "剪贴板同步在线"; continue; }
                if (clip.Envelope is not null && pending is null && started == GetClipboardSequenceNumber())
                {
                    string text = Utf8.GetString(Relay.Open(key, "clip", clip.Envelope, ClipLimit));
                    if (text.Length != 0)
                    {
                        applying = true;
                        try { Clipboard.SetText(text); appliedSequence = GetClipboardSequenceNumber(); }
                        finally { applying = false; }
                    }
                }
                revision = clip.Revision;
                status.Text = "剪贴板同步在线";
            }
            catch (Exception error) when (Retryable(error, token))
            {
                status.Text = error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
                    ? "设备已解绑，请重新配对。" : "同步暂不可用，正在重试。";
                await Task.Delay(1500, token);
            }
        }
    }

    async Task Upload(Relay relay, string key, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (pending is not { } outgoing)
            { await pendingChanged.WaitAsync(token); continue; }
            if (DateTimeOffset.UtcNow - outgoing.Created >= TimeSpan.FromHours(24))
            { pending = null; continue; }
            try
            {
                var envelope = Relay.Seal(key, "clip", Utf8.GetBytes(outgoing.Text));
                await relay.Send<object>(HttpMethod.Put, "clip", token, new { id = outgoing.Id, envelope });
                if (pending == outgoing) pending = null;
            }
            catch (Exception error) when (Retryable(error, token)) { await Task.Delay(1500, token); }
        }
    }

    static bool Retryable(Exception error, CancellationToken token) => !token.IsCancellationRequested &&
        error is HttpRequestException or OperationCanceledException or ExternalException or IOException or CryptographicException or FormatException or ArgumentException or JsonException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    async Task DictionaryLoop(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), token);
        while (!token.IsCancellationRequested)
        {
            try { await SyncDictionary(token); }
            catch (Exception error) when (Retryable(error, token)) { }
            await Task.Delay(TimeSpan.FromMinutes(10), token);
        }
    }

    async Task SyncNow()
    {
        if (session is not { ShareDictionary: true }) { status.Text = "请先启用个人词库共享。"; return; }
        await SyncDictionary(link?.Token ?? CancellationToken.None);
        status.Text = "个人词库已检查同步。";
    }

    async Task CheckForUpdates()
    {
        if (updateRequest is not null) return;
        if (session is not { } device)
        {
            updateStatus.Text = "请先绑定服务器，再检查更新。";
            return;
        }

        WindowsUpdate.CleanPreviousDownloads();
        using var request = new CancellationTokenSource();
        updateRequest = request;
        checkUpdate.Enabled = false;
        cancelUpdate.Visible = true;
        string? installer = null;
        bool launched = false;
        try
        {
            updateStatus.Text = "正在检查更新…";
            var relay = new Relay(device.Server, device.Device.Token);
            var release = await WindowsUpdate.Check(relay, request.Token);
            if (release is null)
            {
                updateStatus.Text = $"当前版本 {WindowsUpdate.Current.VersionName} 已是最新版本。";
                return;
            }

            string version = release.Version.VersionName;
            updateStatus.Text = $"可更新至 {version}。";
            if (MessageBox.Show(this, $"可更新至 {version}，安装包约 {release.Size / (1024d * 1024):0.#} MB。现在下载？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            updateStatus.Text = $"正在下载并校验 {version}…";
            installer = await WindowsUpdate.Download(relay, release, request.Token);
            request.Token.ThrowIfCancellationRequested();
            updateStatus.Text = "安装包校验通过。";
            if (MessageBox.Show(this, $"现在安装 {version}？安装时双拼互联会暂时退出。",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                updateStatus.Text = "已取消安装。";
                return;
            }

            var start = new ProcessStartInfo(installer) { UseShellExecute = true };
            start.ArgumentList.Add("--wait-for");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新安装程序。");
            launched = true;
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        { updateStatus.Text = "服务器绑定已失效，请重新绑定后检查更新。"; }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        { updateStatus.Text = "服务器暂时没有可用的更新包。"; }
        catch (HttpRequestException)
        { updateStatus.Text = "无法连接更新服务，请检查网络后重试。"; }
        catch (Exception error) when (error is InvalidDataException or JsonException or CryptographicException or FormatException)
        { updateStatus.Text = "更新信息或安装包校验失败。"; }
        catch (OperationCanceledException)
        { updateStatus.Text = request.IsCancellationRequested ? "已取消更新。" : "更新请求超时，请重试。"; }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { updateStatus.Text = "无法下载或打开更新安装包，请重试。"; }
        finally
        {
            if (!launched && installer is not null) WindowsUpdate.RemoveDownload(installer);
            updateRequest = null;
            if (!IsDisposed)
            {
                checkUpdate.Enabled = true;
                cancelUpdate.Visible = false;
            }
        }

        if (launched)
        {
            exiting = true;
            Close();
        }
    }

    async Task ClearDictionary()
    {
        if (MessageBox.Show(this, "清空电脑学到的词条和词频，并关闭电脑的词库共享？旧词库会备份在本机。", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (session is { } device)
        {
            session = device with { ShareDictionary = false, DownloadedDictionary = 0, UploadedDictionary = null };
            SaveSession();
            await Restart();
        }
        await dictionaryGate.WaitAsync();
        try { await RimeFiles.ClearDictionary(CancellationToken.None); }
        finally { dictionaryGate.Release(); }
        status.Text = "本机个人词库已清空。";
    }

    async Task SyncDictionary(CancellationToken token)
    {
        await dictionaryGate.WaitAsync(token);
        try
        {
            if (session is not { ShareDictionary: true } device) return;
            var relay = new Relay(device.Server, device.Device.Token);
            var bytes = await RimeFiles.Export(token);
            if (bytes is not null)
            {
                string hash = Relay.Encode(SHA256.HashData(bytes));
                if (device.UploadedDictionary != hash)
                {
                    string envelope = Relay.Seal(device.Key, "dictionary", bytes);
                    long version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await relay.Send<object>(HttpMethod.Put, "dictionary", token, new { version, envelope });
                    session = session! with { UploadedDictionary = hash };
                    SaveSession();
                }
            }
            var incoming = await relay.Send<Snapshot>(HttpMethod.Get, $"dictionary?after={device.DownloadedDictionary}", token);
            if (incoming is null) return;
            await RimeFiles.Import(Relay.Open(device.Key, "dictionary", incoming.Envelope, DictionaryLimit), token);
            session = session! with { DownloadedDictionary = incoming.Version };
            SaveSession();
        }
        finally { dictionaryGate.Release(); }
    }

    async Task Connect()
    {
        string code = setupCode.Text.Trim();
        if (code.Length != 43) { status.Text = "请输入服务器启用码。"; return; }
        string address = server.Text.Trim().TrimEnd('/') + "/";
        var relay = new Relay(address, code);
        Device? device;
        try { device = await relay.Send<Device>(HttpMethod.Post, "bootstrap", CancellationToken.None); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            if (MessageBox.Show(this, "恢复会解除已配对设备并清除服务器上的同步数据。继续吗？", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            device = await relay.Send<Device>(HttpMethod.Post, "recover", CancellationToken.None);
        }
        if (device is null) throw new InvalidOperationException();
        link?.Cancel();
        await StopBeforeAccountChange();
        session = new Session(address, device, Relay.Encode(RandomNumberGenerator.GetBytes(32)));
        setupCode.Clear();
        SaveSession();
        status.Text = "已绑定服务器，可配对手机。";
        await Restart();
        await Invite();
    }

    async Task StopBeforeAccountChange()
    {
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) { }
        pending = null;
        revision = 0;
        ClearQr();
    }

    async Task Invite()
    {
        if (session is not { } device) { status.Text = "请先绑定服务器。"; return; }
        var relay = new Relay(device.Server, device.Device.Token);
        try
        {
            var invite = await relay.Send<Invitation>(HttpMethod.Post, "invite", CancellationToken.None);
            if (invite is null) throw new InvalidOperationException();
            string payload = JsonSerializer.Serialize(new Pairing(1, device.Server, invite.Invite, device.Key), Relay.Json);
            using var qrData = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            using var image = new QRCode(qrData);
            ClearQr();
            qr.Image = image.GetGraphic(5);
            qr.Visible = true;
            qrExpiry.Start();
            status.Text = "在安卓的互联设置中扫码，配对码 5 分钟内有效。";
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict)
        { status.Text = "手机已配对。如需更换，请先解除原配对。"; }
    }

    void ClearQr()
    {
        qrExpiry.Stop();
        qr.Visible = false;
        qr.Image?.Dispose();
        qr.Image = null;
    }

    async Task ResetPeer()
    {
        if (session is not { } device) return;
        if (MessageBox.Show(this, "解除手机配对并清除远程同步数据？", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var relay = new Relay(device.Server, device.Device.Token);
        link?.Cancel();
        await StopBeforeAccountChange();
        try
        {
            await relay.Send<object>(HttpMethod.Post, "reset-peer", CancellationToken.None);
            session = device with { Key = Relay.Encode(RandomNumberGenerator.GetBytes(32)), DownloadedDictionary = 0, UploadedDictionary = null };
            SaveSession();
        }
        finally { await Restart(); }
        status.Text = "手机配对已解除。";
    }

    void UpdateControls()
    {
        updating = true;
        if (session is { } device)
        { pause.Checked = device.Paused; share.Checked = device.ShareDictionary; }
        pause.Enabled = share.Enabled = session is not null;
        if (session is not null && server.Text.Length == 0) server.Text = session.Server;
        updating = false;
    }

    static Session? LoadSession()
    {
        if (!File.Exists(StateFile)) return null;
        byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(StateFile), null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Session>(data, Relay.Json);
    }

    void SaveSession()
    {
        Directory.CreateDirectory(DataDirectory);
        byte[] data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session, Relay.Json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StateFile + ".tmp", data);
        File.Move(StateFile + ".tmp", StateFile, overwrite: true);
    }
}
