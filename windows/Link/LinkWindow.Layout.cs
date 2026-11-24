namespace ShuangpinLink;

internal sealed partial class LinkWindow
{
    readonly Panel inputPage = new GlassPage();
    readonly Panel interopPage = new GlassPage();
    readonly Panel updatesPage = new GlassPage();
    readonly TableLayoutPanel qrBox = Stack();
    readonly RadioButton inputTab = new GlassTab { Text = "输入", Glyph = GlassGlyph.Input };
    readonly RadioButton interopTab = new GlassTab { Text = "设备互通", Glyph = GlassGlyph.Devices };
    readonly RadioButton updatesTab = new GlassTab { Text = "更新", Glyph = GlassGlyph.Update };

    void BuildLayout()
    {
        SuspendLayout();
        DoubleBuffered = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "双拼互联设置";
        AccessibleName = Text;
        ClientSize = new Size(680, 610);
        MinimumSize = new Size(460, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10.5F);
        BackColor = Glass.Surface;
        ForeColor = Glass.Ink;
        HandleCreated += (_, _) => Glass.ApplyCaption(Handle);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Icon = Icon;

        var shell = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new GlassPage(header: true) { Dock = DockStyle.Fill, BackColor = Glass.Surface, Padding = new Padding(24, 10, 24, 12), Margin = Padding.Empty };
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

        var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Glass.Surface };
        host.Controls.AddRange([inputPage, interopPage, updatesPage]);
        shell.Controls.Add(host, 0, 1);

        var feedback = Stack(new GlassCard());
        feedback.Dock = DockStyle.Fill;
        feedback.Padding = new Padding(12, 7, 12, 7);
        feedback.Margin = new Padding(24, 0, 24, 10);
        status.ForeColor = Glass.Ink;
        status.AccessibleName = "操作结果";
        status.AccessibleRole = AccessibleRole.StaticText;
        AddRow(feedback, status);
        status.TextChanged += (_, _) => feedback.Visible = status.Text.Length != 0;
        feedback.Visible = status.Text.Length != 0;
        shell.Controls.Add(feedback, 0, 2);

        StyleCheck(learning);
        StyleCheck(fuzzy);
        StyleCheck(englishCorrection);
        StyleCheck(share);
        StyleCheck(pause);
        StyleCheck(recordHistory);
        StyleCheck(syncImages);
        StyleCheck(keepImageMetadata);
        StyleButton(cancelImage, primary: false);
        StyleButton(retryImage, primary: false);

        var trial = new TextBox { ImeMode = ImeMode.NoControl };
        var modes = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modes.Controls.Add(Heading("微软双拼", 12), 0, 0);
        modes.Controls.Add(Heading("普通拼音", 12), 1, 0);
        modes.Controls.Add(Copy("用两个键输入一个音节"), 0, 1);
        modes.Controls.Add(Copy("输入完整拼音"), 1, 1);
        AddCards(inputPage,
            Card("输入方案", modes, Copy("按 Win + 空格选「双拼互联」。打字时按 Ctrl + ` 切换方案。"), Copy("试打"), Input(trial, "试打输入框")),
            Card("输入辅助", fuzzy, englishCorrection, Copy("英文模式下，按空格时修正常见拼写错误。")),
            Card("个人词库", learning, share, Actions(ActionButton("立即同步词库", SyncNow), ActionButton("清空本机词库", ClearDictionary))));

        qrBox.BackColor = Glass.Contrast ? SystemColors.Window : Color.Transparent;
        qrBox.AccessibleName = "手机配对码";
        qr.AccessibleName = "手机扫码用的配对二维码";
        AddRow(qrBox, qr);
        AddRow(qrBox, Copy("在安卓的互联设置中扫码。配对码 5 分钟内有效。"), top: 6);
        qrBox.Visible = false;
        connectionStatus.ForeColor = Glass.AccentText;
        AddCards(interopPage,
            Card("连接服务器", connectionStatus, Copy("服务器地址"), Input(server, "服务器地址"),
                Copy("服务器启用码"), Input(setupCode, "服务器启用码"), Actions(ActionButton("启用或恢复", Connect, primary: true))),
            Card("手机配对", Actions(ActionButton("显示配对码", Invite, primary: true), ActionButton("解除手机配对", ResetPeer)), qrBox),
            Card("剪贴板", Copy("配对后自动加密收发纯文字。带系统隐私标记的内容会被跳过。"), pause, recordHistory,
                Copy("本机加密保存最近 7 天、最多 50 条。暂停同步时也会暂停收录。"),
                Actions(ActionButton("查看剪贴板历史", ShowHistory))),
            Card("图片互传", syncImages, Copy("两端开启后，复制 PNG、JPEG、GIF、WebP 或截图即可传送。每张最多 8 MiB。"),
                keepImageMetadata, Copy("原图可能带有拍摄位置。默认移除附加元数据。"),
                imageStatus, Actions(cancelImage, retryImage)));

        updateStatus.ForeColor = Glass.Muted;
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
        tab.UseVisualStyleBackColor = Glass.Contrast;
        tab.BackColor = Glass.Contrast ? SystemColors.Control : Color.Transparent;
        tab.ForeColor = Glass.Contrast ? SystemColors.ControlText : Glass.Ink;
        tab.CheckedChanged += (_, _) =>
        {
            tab.Invalidate();
            page.Visible = tab.Checked;
            if (tab.Checked) page.BringToFront();
        };
    }

    static void AddCards(Panel page, params Control[] cards)
    {
        page.Dock = DockStyle.Fill;
        page.BackColor = Glass.Surface;
        page.AutoScroll = true;
        page.Padding = new Padding(24, 16, 24, 14);
        var column = Stack();
        foreach (var card in cards) AddRow(column, card, bottom: 12);
        page.Controls.Add(column);
    }

    static TableLayoutPanel Card(string title, params Control[] content)
    {
        var card = Stack(new GlassCard());
        card.Padding = new Padding(18, 14, 18, 14);
        AddRow(card, Heading(title, 14));
        foreach (var control in content) AddRow(card, control, top: 8);
        return card;
    }

    static TableLayoutPanel Stack(TableLayoutPanel? surface = null)
    {
        var stack = surface ?? new TableLayoutPanel();
        stack.AutoSize = true;
        stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        stack.ColumnCount = 1;
        stack.Dock = DockStyle.Top;
        stack.Margin = Padding.Empty;
        stack.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        if (!Glass.Contrast) stack.BackColor = Color.Transparent;
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
        if (!Glass.Contrast) row.BackColor = Color.Transparent;
        row.Controls.AddRange(controls);
        return row;
    }

    static Panel Input(TextBox field, string accessibleName) => new GlassField(field, accessibleName);

    static Label Heading(string text, float size) => new()
    {
        Text = text, ForeColor = Glass.Ink, Font = new Font("Microsoft YaHei UI", size, FontStyle.Bold), AutoSize = true,
        Margin = Padding.Empty, UseMnemonic = false, BackColor = Glass.Contrast ? SystemColors.Window : Color.Transparent
    };

    static Label Copy(string text) => new()
    {
        Text = text, ForeColor = Glass.Muted, AutoSize = true, Margin = Padding.Empty, UseMnemonic = false,
        BackColor = Glass.Contrast ? SystemColors.Window : Color.Transparent
    };

    static void StyleCheck(CheckBox field)
    {
        field.ForeColor = Glass.Ink;
        if (!Glass.Contrast) field.BackColor = Color.Transparent;
        field.MinimumSize = new Size(0, 28);
        field.UseMnemonic = false;
    }

    static void StyleButton(GlassButton button, bool primary)
    {
        button.AutoSize = true;
        button.MinimumSize = new Size(85, 34);
        button.Margin = new Padding(0, 0, 8, 3);
        button.Padding = new Padding(10, 3, 10, 3);
        button.UseVisualStyleBackColor = Glass.Contrast;
        button.FlatStyle = FlatStyle.Flat;
        button.Primary = primary;
        button.BackColor = Glass.Contrast ? SystemColors.Control : Color.Transparent;
        button.ForeColor = Glass.Contrast ? SystemColors.ControlText : Glass.Ink;
        button.UseMnemonic = false;
    }

}
