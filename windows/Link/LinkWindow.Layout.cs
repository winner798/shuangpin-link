namespace ShuangpinLink;

internal sealed partial class LinkWindow
{
    static readonly Color Surface = AccessibleColor(0xF3F6F8, SystemColors.Control);
    static readonly Color Paper = AccessibleColor(0xFFFFFF, SystemColors.Window);
    static readonly Color Ink = AccessibleColor(0x17232B, SystemColors.WindowText);
    static readonly Color Muted = AccessibleColor(0x586A75, SystemColors.WindowText);
    static readonly Color Accent = AccessibleColor(0x007F78, SystemColors.Highlight);
    static readonly Color Border = AccessibleColor(0xD6E1E6, SystemColors.WindowText);
    static readonly Color Mint = AccessibleColor(0xE1F3F0, SystemColors.Window);
    static readonly Color OnAccent = AccessibleColor(0xFFFFFF, SystemColors.HighlightText);

    readonly Panel inputPage = new();
    readonly Panel interopPage = new();
    readonly Panel updatesPage = new();
    readonly TableLayoutPanel qrBox = Stack();
    readonly RadioButton inputTab = new() { Text = "输入" };
    readonly RadioButton interopTab = new() { Text = "设备互通" };
    readonly RadioButton updatesTab = new() { Text = "更新" };

    void BuildLayout()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "双拼互联设置";
        AccessibleName = Text;
        ClientSize = new Size(680, 610);
        MinimumSize = new Size(460, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10.5F);
        BackColor = Surface;
        ForeColor = Ink;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Icon = Icon;

        var shell = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Paper, Padding = new Padding(24, 10, 24, 12), Margin = Padding.Empty };
        var brand = Heading("双拼互联", 20);
        brand.AutoSize = false;
        brand.Dock = DockStyle.Top;
        brand.Height = 42;
        var navigation = Actions(inputTab, interopTab, updatesTab);
        navigation.AccessibleName = "设置分类";
        navigation.Dock = DockStyle.Top;
        header.Controls.Add(navigation);
        header.Controls.Add(brand);
        shell.Controls.Add(header, 0, 0);

        var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Surface };
        host.Controls.AddRange([inputPage, interopPage, updatesPage]);
        shell.Controls.Add(host, 0, 1);

        var feedback = Stack();
        feedback.Dock = DockStyle.Fill;
        feedback.BackColor = Mint;
        feedback.Padding = new Padding(12, 7, 12, 7);
        feedback.Margin = new Padding(24, 0, 24, 10);
        status.ForeColor = Ink;
        status.AccessibleName = "操作结果";
        status.AccessibleRole = AccessibleRole.StaticText;
        AddRow(feedback, status);
        status.TextChanged += (_, _) => feedback.Visible = status.Text.Length != 0;
        feedback.Visible = status.Text.Length != 0;
        shell.Controls.Add(feedback, 0, 2);

        StyleCheck(learning);
        StyleCheck(share);
        StyleCheck(pause);

        var trial = new TextBox { ImeMode = ImeMode.NoControl };
        var modes = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modes.Controls.Add(Heading("微软双拼", 12), 0, 0);
        modes.Controls.Add(Heading("普通拼音", 12), 1, 0);
        modes.Controls.Add(Copy("用两个键输入一个音节"), 0, 1);
        modes.Controls.Add(Copy("输入完整拼音"), 1, 1);
        AddCards(inputPage,
            Card("输入方案", modes, Copy("按 Win + 空格选「小狼毫」。打字时按 Ctrl + `，再从候选窗切换方案。"), Copy("试打"), Input(trial, "试打输入框")),
            Card("个人词库", learning, share, Actions(ActionButton("立即同步词库", SyncNow), ActionButton("清空本机词库", ClearDictionary))));

        qrBox.BackColor = Paper;
        qrBox.AccessibleName = "手机配对码";
        qr.AccessibleName = "手机扫码用的配对二维码";
        AddRow(qrBox, qr);
        AddRow(qrBox, Copy("在安卓的互联设置中扫码。配对码 5 分钟内有效。"), top: 6);
        qrBox.Visible = false;
        connectionStatus.ForeColor = Accent;
        AddCards(interopPage,
            Card("连接服务器", connectionStatus, Copy("服务器地址"), Input(server, "服务器地址"),
                Copy("服务器启用码"), Input(setupCode, "服务器启用码"), Actions(ActionButton("启用或恢复", Connect, primary: true))),
            Card("手机配对", Actions(ActionButton("显示配对码", Invite, primary: true), ActionButton("解除手机配对", ResetPeer)), qrBox),
            Card("剪贴板", pause));

        updateStatus.ForeColor = Muted;
        StyleButton(checkUpdate, primary: true);
        StyleButton(cancelUpdate, primary: false);
        AddCards(updatesPage, Card("软件更新", updateStatus, Actions(checkUpdate, cancelUpdate)));

        HookTab(inputTab, inputPage);
        HookTab(interopTab, interopPage);
        HookTab(updatesTab, updatesPage);
        (session is null ? interopTab : inputTab).Checked = true;
        Controls.Add(shell);
        ResumeLayout(performLayout: true);
    }

    void HookTab(RadioButton tab, Panel page)
    {
        page.Visible = false;
        tab.Appearance = Appearance.Button;
        tab.TextAlign = ContentAlignment.MiddleCenter;
        tab.AutoSize = true;
        tab.MinimumSize = new Size(76, 35);
        tab.Margin = new Padding(0, 0, 8, 0);
        tab.Padding = new Padding(10, 3, 10, 3);
        tab.FlatStyle = FlatStyle.Flat;
        tab.UseVisualStyleBackColor = false;
        tab.FlatAppearance.BorderSize = 1;
        tab.FlatAppearance.BorderColor = Border;
        tab.FlatAppearance.CheckedBackColor = Accent;
        tab.FlatAppearance.MouseOverBackColor = Mint;
        tab.FlatAppearance.MouseDownBackColor = Mint;
        tab.BackColor = Paper;
        tab.ForeColor = Ink;
        tab.CheckedChanged += (_, _) =>
        {
            tab.ForeColor = tab.Checked ? OnAccent : Ink;
            tab.FlatAppearance.BorderColor = tab.Checked ? Accent : Border;
            tab.FlatAppearance.MouseOverBackColor = tab.Checked ? Accent : Mint;
            tab.FlatAppearance.MouseDownBackColor = tab.Checked ? Accent : Mint;
            page.Visible = tab.Checked;
            if (tab.Checked) page.BringToFront();
        };
    }

    static void AddCards(Panel page, params Control[] cards)
    {
        page.Dock = DockStyle.Fill;
        page.BackColor = Surface;
        page.AutoScroll = true;
        page.Padding = new Padding(24, 16, 24, 14);
        var column = Stack();
        foreach (var card in cards) AddRow(column, card, bottom: 12);
        page.Controls.Add(column);
    }

    static TableLayoutPanel Card(string title, params Control[] content)
    {
        var frame = Stack();
        frame.BackColor = Border;
        frame.Padding = new Padding(1);
        var inner = Stack();
        inner.BackColor = Paper;
        inner.Padding = new Padding(16, 13, 16, 13);
        AddRow(inner, Heading(title, 14));
        foreach (var control in content) AddRow(inner, control, top: 8);
        AddRow(frame, inner);
        return frame;
    }

    static TableLayoutPanel Stack()
    {
        var stack = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top,
            Margin = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    static void AddRow(TableLayoutPanel stack, Control control, int top = 0, int bottom = 0)
    {
        int row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, top, 0, bottom);
        control.Anchor = control is PictureBox ? AnchorStyles.Left : AnchorStyles.Left | AnchorStyles.Right;
        if (control is Label label)
        {
            // A finite width lets feedback and help text wrap at narrow window sizes.
            void WrapLabel()
            {
                int width = stack.ClientSize.Width - stack.Padding.Horizontal;
                if (width >= 140 && width != label.MaximumSize.Width) label.MaximumSize = new Size(width, 0);
            }
            stack.ClientSizeChanged += (_, _) => WrapLabel();
        }
        stack.Controls.Add(control, 0, row);
    }

    static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Padding = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    static Panel Input(TextBox field, string accessibleName)
    {
        field.AccessibleName = accessibleName;
        field.BackColor = Paper;
        field.ForeColor = Ink;
        field.BorderStyle = BorderStyle.None;
        field.Dock = DockStyle.Top;
        var outline = new Panel { BackColor = Border, Padding = new Padding(1), Height = 36 };
        var inside = new Panel { BackColor = Paper, Padding = new Padding(9, 7, 9, 6), Dock = DockStyle.Fill };
        inside.Controls.Add(field);
        outline.Controls.Add(inside);
        field.Enter += (_, _) => outline.BackColor = Accent;
        field.Leave += (_, _) => outline.BackColor = Border;
        return outline;
    }

    static Label Heading(string text, float size) => new()
    {
        Text = text, ForeColor = Ink, Font = new Font("Microsoft YaHei UI", size, FontStyle.Bold), AutoSize = true,
        Margin = Padding.Empty, UseMnemonic = false
    };

    static Label Copy(string text) => new() { Text = text, ForeColor = Muted, AutoSize = true, Margin = Padding.Empty, UseMnemonic = false };

    static void StyleCheck(CheckBox field)
    {
        field.ForeColor = Ink;
        field.MinimumSize = new Size(0, 28);
        field.UseMnemonic = false;
    }

    static void StyleButton(Button button, bool primary)
    {
        button.AutoSize = true;
        button.MinimumSize = new Size(85, 34);
        button.Margin = new Padding(0, 0, 8, 3);
        button.Padding = new Padding(10, 3, 10, 3);
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? AccessibleColor(0x00635D, SystemColors.Highlight) : Mint;
        button.FlatAppearance.MouseDownBackColor = button.FlatAppearance.MouseOverBackColor;
        button.BackColor = primary ? Accent : Paper;
        button.ForeColor = primary ? OnAccent : Ink;
        button.UseMnemonic = false;
    }

    static Color AccessibleColor(int rgb, Color fallback) => SystemInformation.HighContrast
        ? fallback : Color.FromArgb(unchecked((int)0xFF000000) | rgb);
}
