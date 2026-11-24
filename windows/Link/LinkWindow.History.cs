using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace ShuangpinLink;

internal sealed partial class LinkWindow
{
    readonly ClipboardHistory history = new(Path.Combine(DataDirectory, "clipboard-history.bin"));
    readonly CheckBox recordHistory = new GlassCheckBox { AutoSize = true, Text = "保存本机剪贴板历史" };
    readonly System.Windows.Forms.Timer historyExpiry = new() { Interval = 60_000 };
    HistoryWindow? historyWindow;
    bool sessionLocked;

    void ConfigureHistory()
    {
        try { recordHistory.Checked = Registry.GetValue(ImePreferences, "ClipboardHistory", 1) is not 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { recordHistory.Checked = false; }
        recordHistory.CheckedChanged += (_, _) =>
        {
            try { Registry.SetValue(ImePreferences, "ClipboardHistory", recordHistory.Checked ? 1 : 0); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { status.Text = "历史记录开关已在本次运行中生效，暂时无法保存设置。"; }
        };
        historyExpiry.Tick += async (_, _) => await CheckHistory();
        historyExpiry.Start();
        _ = CheckHistory();
    }

    static bool HistoryError(Exception error) =>
        error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException;
    bool CanRecordHistory => recordHistory.Checked && session is not { Paused: true };
    async Task AddHistory(string text)
    {
        if (!CanRecordHistory) return;
        try
        {
            await history.Add(text);
            if (!IsDisposed && historyWindow is { IsDisposed: false } view) await view.RefreshEntries();
        }
        catch (Exception error) when (HistoryError(error))
        { if (!IsDisposed) status.Text = "无法保存剪贴板历史。"; }
    }

    async Task CheckHistory()
    {
        try
        {
            if (historyWindow is { IsDisposed: false } view) await view.RefreshEntries();
            else await history.Prune();
        }
        catch (Exception error) when (HistoryError(error))
        { if (!IsDisposed) status.Text = "无法读取剪贴板历史。"; }
    }

    Task ShowHistory()
    {
        if (!InteractiveDesktop()) return Task.CompletedTask;
        if (historyWindow is not { IsDisposed: false } view)
        {
            historyWindow = new HistoryWindow(this);
            historyWindow.FormClosed += (_, _) => historyWindow = null;
            historyWindow.Show(this);
        }
        else view.Activate();
        return Task.CompletedTask;
    }

    void CloseHistory()
    {
        historyExpiry.Stop();
        historyExpiry.Dispose();
        historyWindow?.Close();
        try { history.Drain().GetAwaiter().GetResult(); }
        catch (Exception error) when (HistoryError(error)) { }
    }

    // The input desktop differs from the user's desktop while locked or on a secure prompt.
    bool InteractiveDesktop()
    {
        if (sessionLocked) return false;
        nint desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0) return false;
        try
        {
            var name = new System.Text.StringBuilder(128);
            return GetUserObjectInformationW(desktop, 2, name, name.Capacity * sizeof(char), out _)
                && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("user32.dll")] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool GetUserObjectInformationW(nint handle, int index, System.Text.StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(nint hwnd);

    sealed class HistoryWindow : Form
    {
        readonly LinkWindow owner;
        readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "剪贴板条目" };
        readonly TextBox detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "选中条目全文" };
        readonly Label message = Copy("正在读取…");
        readonly GlassButton copy = new() { Text = "复制全文" };
        readonly GlassButton delete = new() { Text = "删除此条" };
        readonly GlassButton clear = new() { Text = "清空历史" };
        int revision;

        internal HistoryWindow(LinkWindow owner)
        {
            this.owner = owner;
            Text = "剪贴板历史";
            Icon = owner.Icon;
            Font = owner.Font;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(540, 480);
            MinimumSize = new Size(395, 330);
            BackColor = Glass.Surface;
            ForeColor = Glass.Ink;
            HandleCreated += (_, _) => Glass.ApplyCaption(Handle);
            var page = new GlassCard { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = new Padding(20) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var row in new RowStyle[] { new(SizeType.AutoSize), new(SizeType.Percent, 55), new(SizeType.Percent, 45),
                        new(SizeType.AutoSize), new(SizeType.AutoSize) }) page.RowStyles.Add(row);
            page.Controls.Add(Heading("剪贴板历史", 16), 0, 0);
            list.BackColor = detail.BackColor = Glass.Field;
            list.ForeColor = detail.ForeColor = Glass.Ink;
            detail.BorderStyle = list.BorderStyle = BorderStyle.FixedSingle;
            list.Margin = new Padding(0, 12, 0, 8);
            detail.Margin = new Padding(0, 0, 0, 8);
            page.Controls.Add(list, 0, 1);
            page.Controls.Add(detail, 0, 2);
            page.Controls.Add(message, 0, 3);
            StyleButton(copy, true);
            StyleButton(delete, false);
            StyleButton(clear, false);
            page.Controls.Add(Actions(copy, delete, clear), 0, 4);
            Controls.Add(page);
            list.FormattingEnabled = true;
            list.Format += (_, args) =>
            {
                if (args.ListItem is HistoryEntry item)
                {
                    var prefix = item.Text.Length <= 100 ? item.Text : item.Text[..100] + "…";
                    var when = DateTimeOffset.FromUnixTimeMilliseconds(item.CopiedAt).LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
                    args.Value = when + "   " + prefix.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                }
            };
            list.SelectedIndexChanged += (_, _) => UpdateSelection();
            copy.Click += (_, _) => CopySelected();
            list.DoubleClick += (_, _) => CopySelected();
            delete.Click += async (_, _) =>
            {
                if (list.SelectedItem is HistoryEntry item) await Mutate(() => owner.history.Delete(item.Id));
            };
            clear.Click += async (_, _) =>
            {
                if (MessageBox.Show(this, "清空本机所有剪贴板历史？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes) await Mutate(owner.history.Clear);
            };
            Shown += async (_, _) => await RefreshEntries();
            UpdateSelection();
        }

        async Task Mutate(Func<Task> action)
        {
            try { await action(); if (!IsDisposed) await RefreshEntries(); }
            catch (Exception error) when (HistoryError(error))
            { if (!IsDisposed) message.Text = "操作失败，请重试。"; }
        }

        void CopySelected()
        {
            if (list.SelectedItem is not HistoryEntry entry || !owner.InteractiveDesktop()) return;
            if (entry.CopiedAt <= (DateTimeOffset.UtcNow - ClipboardHistory.Retention).ToUnixTimeMilliseconds())
            {
                _ = RefreshEntries();
                return;
            }
            try
            {
                bool copied = PrivateClipboard.Write(Handle, PrivateClipboard.Sequence, entry.Text);
                message.Text = copied ? "已复制，可以粘贴。" : "剪贴板已变化，请重试。";
            }
            catch (ExternalException) { message.Text = "剪贴板正在使用，请重试。"; }
        }

        void UpdateSelection()
        {
            var entry = list.SelectedItem as HistoryEntry;
            detail.Text = entry?.Text ?? "";
            copy.Enabled = delete.Enabled = entry is not null;
            clear.Enabled = list.Items.Count > 0;
        }

        internal async Task RefreshEntries()
        {
            int current = ++revision;
            try
            {
                var entries = await owner.history.List();
                if (IsDisposed || current != revision || !owner.InteractiveDesktop()) return;
                var previous = (list.SelectedItem as HistoryEntry)?.Id;
                list.BeginUpdate();
                try
                {
                    list.Items.Clear();
                    list.Items.AddRange(entries.ToArray());
                    if (entries.Count > 0) list.SelectedIndex = Math.Max(0, entries.FindIndex(entry => entry.Id == previous));
                }
                finally { list.EndUpdate(); }
                UpdateSelection();
                message.Text = entries.Count == 0 ? "暂无历史记录" : $"最近 7 天 · {entries.Count} 条";
            }
            catch (Exception error) when (HistoryError(error))
            {
                if (!IsDisposed)
                {
                    message.Text = "历史记录无法读取，可以清空后重新开始。";
                    clear.Enabled = true;
                }
            }
        }
    }
}
