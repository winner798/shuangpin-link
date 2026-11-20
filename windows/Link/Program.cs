using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ShuangpinLink.ShowSettings");
        bool startHidden = args.Contains("--tray");
        if (!first)
        {
            if (!startHidden) showSettings.Set();
            return;
        }
        if (InputPaths.InstalledEngine() is { } engine)
        {
            try { InputPaths.StartTool(engine, InputPaths.Server).Dispose(); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { }
        }
        ApplicationConfiguration.Initialize();
        using var window = new LinkWindow(startHidden);
        _ = window.Handle;
        var activation = ThreadPool.RegisterWaitForSingleObject(showSettings, (_, _) =>
        {
            try { window.BeginInvoke(window.Reveal); }
            catch (InvalidOperationException) { }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        try { Application.Run(window); }
        finally { activation.Unregister(null); }
    }
}

internal sealed partial class LinkWindow : Form
{
    static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShuangpinLink", "state");
    static readonly string StateFile = Path.Combine(DataDirectory, "device.bin");
    const int DictionaryLimit = 8 * 1024 * 1024;
    readonly TextBox server = new() { Text = "https://1.15.223.20/ime-sync/" };
    readonly TextBox setupCode = new() { UseSystemPasswordChar = true };
    readonly PictureBox qr = new() { Size = new Size(204, 204), SizeMode = PictureBoxSizeMode.Zoom, TabStop = false };
    readonly Label status = new() { AutoSize = true };
    readonly Label connectionStatus = new() { AutoSize = true, Text = "尚未绑定服务器" };
    readonly CheckBox pause = new GlassCheckBox { AutoSize = true, Text = "暂停剪贴板同步" };
    readonly CheckBox share = new GlassCheckBox { AutoSize = true, Text = "共享个人词库", Checked = true };
    readonly CheckBox learning = new GlassCheckBox { AutoSize = true, Text = "学习选词习惯", Checked = true };
    readonly GlassButton checkUpdate = new() { Text = "检查更新" };
    readonly GlassButton cancelUpdate = new() { Text = "取消更新", Visible = false };
    readonly Label updateStatus = new() { AutoSize = true, Text = $"当前版本 {WindowsUpdate.Current.VersionName}" };
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
    bool learningEnabled = true;
    readonly SemaphoreSlim dictionaryGate = new(1, 1);

    internal LinkWindow(bool startHidden)
    {
        this.startHidden = startHidden;
        try { session = LoadSession(); }
        catch (Exception error) when (error is IOException or CryptographicException or JsonException)
        { status.Text = "设备设置无法读取，请用服务器启用码恢复。"; }
        try { learningEnabled = RimeFiles.LearningEnabled; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { status.Text = "无法读取词库的学习设置。"; }
        if (session is { } device) server.Text = device.Server;

        BuildLayout();
        checkUpdate.Click += async (_, _) => await CheckForUpdates();
        cancelUpdate.Click += (_, _) => updateRequest?.Cancel();

        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("打开设置", null, (_, _) => Reveal());
        tray.ContextMenuStrip.Items.Add("检查更新", null, async (_, _) => { Reveal(); updatesTab.Checked = true; await CheckForUpdates(); });
        tray.ContextMenuStrip.Items.Add("暂停或恢复剪贴板互通", null, (_, _) => pause.Checked = !pause.Checked);
        tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray.DoubleClick += (_, _) => Reveal();
        qrExpiry.Tick += (_, _) => ClearQr();

        UpdateControls();
        pause.CheckedChanged += (_, _) =>
        {
            if (updating || session is null) return;
            session = session with { Paused = pause.Checked };
            RestartClipboard();
            try { SaveSession(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            { status.Text = "剪贴板开关在本次运行中已生效，但暂时无法保存。"; }
            UpdateControls();
        };
        share.CheckedChanged += async (_, _) => await RunAction(async () =>
        {
            if (updating || session is null) return;
            session = session with { ShareDictionary = share.Checked };
            SaveSession();
            await Restart();
        });
        learning.CheckedChanged += async (_, _) =>
        {
            if (updating) return;
            if (busy)
            {
                UpdateControls();
                return;
            }
            bool enabled = learning.Checked;
            await RunAction(async () =>
            {
                await dictionaryGate.WaitAsync();
                try
                {
                    await RimeFiles.SetLearning(enabled, CancellationToken.None);
                    learningEnabled = enabled;
                    status.Text = enabled ? "两种输入方案的选词学习已启用。" : "两种输入方案的选词学习已暂停。";
                }
                finally { dictionaryGate.Release(); }
            });
        };
    }

    Button ActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = new GlassButton { Text = label };
        StyleButton(button, primary);
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
        catch (InvalidDataException error) { status.Text = error.Message; }
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
        RestartClipboard();
    }

    internal void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        { e.Cancel = true; Hide(); return; }
        StopClipboard();
        link?.Cancel();
        updateRequest?.Cancel();
        ClearQr();
        tray.Dispose();
        base.OnFormClosing(e);
    }

    async Task Restart()
    {
        link?.Cancel();
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) { }
        link?.Dispose();
        link = null;
        workers = [];
        if (session is not { ShareDictionary: true }) return;
        link = new CancellationTokenSource();
        workers = [DictionaryLoop(link.Token)];
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
            var release = await WindowsUpdate.Check(request.Token);
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
            installer = await WindowsUpdate.Download(release, request.Token);
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
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        { updateStatus.Text = "暂时没有可用的更新包。"; }
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
        try
        {
            await StopBeforeAccountChange();
            session = new Session(address, device, Relay.Encode(RandomNumberGenerator.GetBytes(32)));
            setupCode.Clear();
            SaveSession();
            status.Text = "已绑定服务器，可配对手机。";
            await Restart();
        }
        finally { changingClipboardPair = false; RestartClipboard(); }
        await Invite();
    }

    async Task StopBeforeAccountChange()
    {
        changingClipboardPair = true;
        StopClipboard();
        lastClipboardSentAt = 0;
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) { }
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
            qrBox.Visible = true;
            interopPage.ScrollControlIntoView(qrBox);
            qrExpiry.Start();
            status.Text = "配对码已生成。";
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict)
        { status.Text = "手机已配对。如需更换，请先解除原配对。"; }
    }

    void ClearQr()
    {
        qrExpiry.Stop();
        qrBox.Visible = false;
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
        try
        {
            await StopBeforeAccountChange();
            await relay.Send<object>(HttpMethod.Post, "reset-peer", CancellationToken.None);
            session = session! with { Key = Relay.Encode(RandomNumberGenerator.GetBytes(32)), DownloadedDictionary = 0,
                UploadedDictionary = null, AcceptedClipboardTime = 0 };
            SaveSession();
        }
        finally { changingClipboardPair = false; await Restart(); RestartClipboard(); }
        status.Text = "手机配对已解除。";
    }

    void UpdateControls()
    {
        updating = true;
        if (session is { } device)
        {
            pause.Checked = device.Paused;
            share.Checked = device.ShareDictionary;
        }
        if (session is null) connectionStatus.Text = "尚未绑定服务器";
        else if (session.Paused) connectionStatus.Text = "剪贴板互通已暂停";
        else if (!clipboardPeerReady) connectionStatus.Text = "已绑定服务器，正在确认配对";
        learning.Checked = learningEnabled;
        pause.Enabled = share.Enabled = session is not null;
        if (session is not null && server.Text.Length == 0) server.Text = session.Server;
        updating = false;
    }

    static Session? LoadSession()
    {
        if (!File.Exists(StateFile)) return null;
        byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(StateFile), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Session>(data, Relay.Json); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    void SaveSession()
    {
        Directory.CreateDirectory(DataDirectory);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(session, Relay.Json);
        byte[] data;
        try { data = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        File.WriteAllBytes(StateFile + ".tmp", data);
        File.Move(StateFile + ".tmp", StateFile, overwrite: true);
    }
}
