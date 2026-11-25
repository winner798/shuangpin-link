using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using ShuangpinLink;

internal static class Program
{
    private static readonly Guid ProductClass = new("a6d4a312-7df3-4bd0-9d84-bbd25bc60aa1");
    private static readonly Guid ProductProfile = new("40cfbb46-1eb1-41f3-a336-75cb9a137bd2");
    private static readonly Guid LegacyClass = new("a3f4cded-b1e9-41ee-9ca6-7b4d0de6cb0a");
    private static readonly Guid LegacyProfile = new("3d02cab6-2b8e-4781-ba20-1c9267529467");
    private static readonly Guid ProfileManager = new("33c53a50-f456-4884-b049-85fd643ecfed");
    private static readonly Guid CategoryManager = new("a4b544a1-438d-4b41-9325-869523e2d6c7");
    private static readonly Guid KeyboardCategory = new("34745c63-b2f0-4784-8b67-5e12c8701a31");
    private const ushort SimplifiedChinese = 0x0804;
    private const ushort TraditionalChinese = 0x0404;
    private const uint TextServiceProfile = 1;
    private const uint ForProcess = 0x10000000;
    private const uint IgnoreCurrentInputLanguage = 0x00000004;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args is ["--seed-image-pair", var pairing]) return ImageNetworkFixture.Seed(pairing);
        if (args is ["--image-roundtrip", var pair]) return ImageNetworkFixture.Run(pair);
        if (args is ["--check-history", var folder])
        {
            HistoryFixture.Run(folder).GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--seed-legacy-tip"] or ["--assert-legacy-tip-hidden"])
            return LegacyTipFixture(seed: args[0] == "--seed-legacy-tip");
        if (args.Length != 2)
            throw new ArgumentException("Usage: Editor <screenshot-directory> <installed-TSF-DLL>");

        // Enable the installed service's mode-only trace in this test process.
        Environment.SetEnvironmentVariable("SHUANGPIN_LINK_INPUT_TEST_TRACE", "1");
        DescribeDesktops();
        ApplicationConfiguration.Initialize();
        using var window = new Form
        {
            Text = "双拼互联 Windows 输入验证",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(680, 330),
            Padding = new Padding(28, 40, 28, 28),
        };
        using var editor = new RichTextBox
        {
            Name = "IndependentEditor",
            Dock = DockStyle.Fill,
            DetectUrls = false,
            Font = new Font("Microsoft YaHei UI", 19),
        };
        window.Controls.Add(editor);
        int status = 1;
        window.Shown += async (_, _) =>
        {
            try
            {
                await EnsureFocus(window, editor);
                object manager = CreateComObject(ProfileManager);
                try
                {
                    var profiles = (ITfInputProcessorProfiles)manager;
                    Marshal.ThrowExceptionForHR(profiles.GetLanguageProfileDescription(
                        ProductClass, SimplifiedChinese, ProductProfile, out string description));
                    if (description.TrimEnd('\0') != "双拼互联")
                        throw new InvalidOperationException($"Windows shows an unexpected input method name: {description}");
                    Marshal.ThrowExceptionForHR(profiles.IsEnabledLanguageProfile(
                        ProductClass, SimplifiedChinese, ProductProfile, out bool enabled));
                    if (!enabled)
                        throw new InvalidOperationException("Setup did not enable 双拼互联 for the Windows user.");

                    var active = (ITfInputProcessorProfileManager)manager;
                    Marshal.ThrowExceptionForHR(active.ActivateProfile(TextServiceProfile, SimplifiedChinese,
                        ProductClass, ProductProfile, IntPtr.Zero, ForProcess | IgnoreCurrentInputLanguage));
                    await WaitFor(() => active.GetActiveProfile(KeyboardCategory, out var profile) == 0
                        && profile.ClassId == ProductClass && profile.ProfileId == ProductProfile,
                        "Windows did not activate the registered 双拼互联 text service in the editor.");
                    await WaitFor(() => LoadedModule(args[1]), "The registered 双拼互联 DLL was not loaded by the editor.");
                    Console.WriteLine($"Windows activated 双拼互联 ({ProductClass}, {ProductProfile}) and loaded {args[1]}.");

                    await AssertCommit(window, editor, args[0], "nihk", "microsoft-double-pinyin", Keys.Left);

                    await SendChord(window, editor, Keys.ControlKey, Keys.Oemtilde);
                    await Task.Delay(600);
                    SaveScreen(window, args[0], "input-scheme-picker");
                    // In the Rime scheme picker, pressing the hotkey again advances to the next scheme.
                    await SendChord(window, editor, Keys.ControlKey, Keys.Oemtilde);
                    await Task.Delay(180);
                    await SendChord(window, editor, Keys.Enter);
                    await Task.Delay(550);

                    await AssertCommit(window, editor, args[0], "nihao", "full-pinyin", Keys.Right);
                    await AssertPassiveSuggestions(window, editor);
                    await AssertEnglishCorrection(window, editor, args[0]);
                    using (var masked = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true })
                    {
                        window.Controls.Add(masked);
                        masked.BringToFront();
                        await EnsureFocus(window, masked);
                        foreach (var key in new[] { Keys.N, Keys.I, Keys.H, Keys.A, Keys.O, Keys.Space })
                        {
                            await SendChord(window, masked, key);
                            await Task.Delay(80);
                        }
                        await WaitFor(() => masked.Text == "nihao ", "A password field did not receive literal keys.");
                        editor.Focus();
                    }
                    await HistoryFixture.Run(args[0]);
                    await AssertClipboardPrivacy(window.Handle);
                    ImageFixture.VerifyClipboard(window.Handle, args[0]);
                    status = 0;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(manager);
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine($"Editor text at failure: [{editor.Text}]");
                DescribeProfile(editor, "at failure");
                if (GetForegroundWindow() == window.Handle)
                    SaveScreen(window, args[0], "failure");
            }
            finally
            {
                window.Close();
            }
        };
        Application.Run(window);
        return status;
    }

    private static object CreateComObject(Guid clsid) =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;

    private static void DescribeProfile(RichTextBox editor, string stage)
    {
        object manager = CreateComObject(ProfileManager);
        try
        {
            int status = ((ITfInputProcessorProfileManager)manager).GetActiveProfile(KeyboardCategory, out var active);
            Console.WriteLine($"Editor profile {stage}: {status:x8}, {active.ClassId}, {active.ProfileId}, focused={editor.Focused}.");
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    private static async Task AssertClipboardPrivacy(nint window)
    {
        const string protectedText = "剪贴板隐私测试";
        const string newerText = "本机刚复制的测试文字";
        try
        {
            uint original = PrivateClipboard.Sequence;
            if (!PrivateClipboard.Write(window, original, protectedText))
                throw new InvalidOperationException("The synthetic clipboard changed unexpectedly.");
            if (Clipboard.GetText() != protectedText || Clipboard.GetDataObject() is not { } data || !PrivateClipboard.IsProtected(data))
                throw new InvalidOperationException("The clipboard write did not preserve text and its system privacy marker.");
            await Task.Delay(300);
            uint beforeLocalCopy = PrivateClipboard.Sequence;
            Clipboard.SetText(newerText);
            if (PrivateClipboard.Write(window, beforeLocalCopy, protectedText) || Clipboard.GetText() != newerText)
                throw new InvalidOperationException("Receiving text overwrote a newer local clipboard entry.");
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShuangpinLink", "state", "clipboard-history.bin");
            await WaitFor(() =>
            {
                try { return ClipboardHistory.ReadSnapshot(path).Any(entry => entry.Text == newerText); }
                catch (IOException) { return false; }
            }, "The installed tray application did not capture a local copy without pairing.");
            if (ClipboardHistory.ReadSnapshot(path).Any(entry => entry.Text == protectedText))
                throw new InvalidOperationException("A privacy-marked copy entered local history.");
            Console.WriteLine("Unpaired clipboard capture and sensitive copy exclusion verified.");
            Console.WriteLine("Clipboard privacy marker and protection against overwriting a newer copy verified.");
        }
        finally { Clipboard.Clear(); }
    }

    private static int LegacyTipFixture(bool seed)
    {
        try
        {
            const string label = "小狼毫（测试占位）";
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var legacyComServer = $@"SOFTWARE\Classes\CLSID\{LegacyClass:B}\InprocServer32";
            if (seed)
            {
                using var existing = machine.OpenSubKey(legacyComServer);
                if (existing is not null)
                    throw new InvalidOperationException("An actual legacy IME is already installed in this VM.");

                // The system COM DLL rejects the fixture's class ID; no old IME binaries are loaded.
                using var inertServer = machine.CreateSubKey(legacyComServer);
                inertServer.SetValue(null, Path.Combine(Environment.SystemDirectory, "msctf.dll"));
                inertServer.SetValue("ThreadingModel", "Apartment");
            }

            var manager = CreateComObject(ProfileManager);
            try
            {
                if (seed)
                {
                    var categoryManager = CreateComObject(CategoryManager);
                    try
                    {
                        Marshal.ThrowExceptionForHR(((ITfCategoryManager)categoryManager).RegisterCategory(
                            LegacyClass, KeyboardCategory, LegacyClass));
                    }
                    finally { Marshal.FinalReleaseComObject(categoryManager); }
                }

                foreach (ushort language in new[] { SimplifiedChinese, TraditionalChinese })
                {
                    var profiles = (ITfInputProcessorProfiles)manager;
                    // The hosted runner can enable the zh-CN TIP without adding another Windows language.
                    bool expectedEnabled = seed && language == SimplifiedChinese;
                    if (seed)
                    {
                        string icon = Path.Combine(Environment.SystemDirectory, "shell32.dll");
                        Marshal.ThrowExceptionForHR(((ITfInputProcessorProfileManager)manager).RegisterProfile(
                            LegacyClass, language, LegacyProfile, label, (uint)label.Length, icon,
                            (uint)icon.Length, 0, IntPtr.Zero, 0, false, 0));
                        if (expectedEnabled && !InstallLayoutOrTip($"0x{language:X4}:{LegacyClass:B}{LegacyProfile:B}", 0))
                            throw new InvalidOperationException($"Windows could not enable fixture TIP 0x{language:X4}.");
                    }

                    Marshal.ThrowExceptionForHR(profiles.GetLanguageProfileDescription(
                        LegacyClass, language, LegacyProfile, out string description));
                    Marshal.ThrowExceptionForHR(profiles.IsEnabledLanguageProfile(
                        LegacyClass, language, LegacyProfile, out bool enabled));
                    if (description.TrimEnd('\0') != label || enabled != expectedEnabled)
                        throw new InvalidOperationException($"Fixture TIP 0x{language:X4} unexpectedly has enabled={enabled} and label [{description}].");
                    Console.WriteLine($"Fixture TIP 0x{language:X4} remains registered and user enabled={enabled}.");
                }
            }
            finally { Marshal.FinalReleaseComObject(manager); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task AssertCommit(Form window, RichTextBox editor, string output, string keys, string name, Keys arrow)
    {
        if (editor.Text.Length != 0)
            throw new InvalidOperationException($"The editor was not empty before {name}: [{editor.Text}]");
        await EnsureFocus(window, editor);
        // Leave editable text on both sides so a missed or duplicated arrow is observable.
        editor.Text = "甲乙丙";
        editor.Select(1, 0);
        DescribeProfile(editor, $"before {name}");

        await TypeCode(window, editor, keys);
        await Task.Delay(430);
        SaveScreen(window, output, name + "-candidates");
        await SendChord(window, editor, Keys.Space);
        const string original = "甲你好乙丙";
        await WaitFor(() => editor.Text == original && editor.SelectionStart == 3,
            $"{name} did not commit 你好 at the expected position.");
        await WaitFor(CandidateWindowVisible, $"{name} did not show the post-commit prediction window.");
        SaveScreen(window, output, name + "-committed");
        await SendChord(window, editor, arrow);
        int nextCaret = arrow == Keys.Left ? 2 : 4;
        await WaitFor(() => editor.Text == original && editor.SelectionStart == nextCaret
            && editor.SelectionLength == 0 && !CandidateWindowVisible(),
            $"{name} {arrow} did not dismiss suggestions and move the caret exactly once (caret {editor.SelectionStart}).");
        await TypeCode(window, editor, keys);
        await SendChord(window, editor, Keys.Space);
        await WaitFor(() => editor.Text == original.Insert(nextCaret, "你好"), $"{name} could not continue Chinese input at the new caret.");
        await SendChord(window, editor, Keys.Escape);
        await WaitFor(() => !CandidateWindowVisible(), $"{name} did not close the final candidate list.");
        Console.WriteLine($"{name}: {arrow} closed the visible prediction list, moved the caret once, and kept Chinese typing active.");
        editor.Clear();
        foreach (bool keypad in new[] { false, true })
        {
            await TypeCode(window, editor, keys);
            await WaitFor(CandidateWindowVisible, "Expected a spelling before Return.");
            await SendReturn(window, editor, keypad);
            await WaitFor(() => editor.Text == keys && !CandidateWindowVisible(),
                $"{name}: Return did not commit the raw spelling and close the menu.");
            await TypeCode(window, editor, keys);
            await SendChord(window, editor, Keys.Space);
            await WaitFor(() => editor.Text == keys + "你好" && CandidateWindowVisible(),
                $"{name}: expected suggestions before Return.");
            await SendReturn(window, editor, keypad);
            await WaitFor(() => editor.Text.Replace("\r\n", "\n") == keys + "你好\n" && !CandidateWindowVisible(),
                $"{name}: Return did not dismiss the suggestion and deliver one newline.");
            await TypeCode(window, editor, keys);
            await SendChord(window, editor, Keys.Space);
            await WaitFor(() => editor.Text.Replace("\r\n", "\n") == keys + "你好\n你好",
                $"{name}: Chinese input did not resume after Return.");
            await SendChord(window, editor, Keys.Escape);
            editor.Clear();
        }
        Console.WriteLine($"{name}: main and keypad Enter closed spelling and prediction windows and Chinese input resumed.");
        await SendChord(window, editor, Keys.ShiftKey, Keys.V);
        await SendChord(window, editor, Keys.D2);
        await SendChord(window, editor, Keys.Space);
        await WaitFor(() => editor.Text == "V2" && !CandidateWindowVisible(),
            $"{name}: Space did not confirm the literal identifier V2 exactly.");
        editor.Clear();
    }

    private static async Task SendReturn(Form window, Control editor, bool keypad)
    {
        await EnsureFocus(window, editor);
        Input[] inputs = [KeyInput(Keys.Enter, false, keypad), KeyInput(Keys.Enter, true, keypad)];
        if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static async Task TypeCode(Form window, RichTextBox editor, string keys)
    {
        foreach (char key in keys)
        {
            await SendChord(window, editor, (Keys)char.ToUpperInvariant(key));
            await Task.Delay(90);
        }
    }

    private static async Task AssertPassiveSuggestions(Form window, RichTextBox editor)
    {
        const string committed = "甲你好乙";
        async Task OpenSuggestions()
        {
            editor.Text = "甲乙";
            editor.Select(1, 0);
            await TypeCode(window, editor, "nihao");
            await SendChord(window, editor, Keys.Space);
            await WaitFor(() => editor.Text == committed && editor.SelectionStart == 3 && CandidateWindowVisible(),
                "Expected a committed greeting and visible continuations.");
        }

        await OpenSuggestions();
        await SendChord(window, editor, Keys.Space);
        await WaitFor(() => editor.Text == "甲你好 乙" && !CandidateWindowVisible(), "Space selected a passive suggestion.");

        await OpenSuggestions();
        Point caret = editor.GetPositionFromCharIndex(3);
        ClickAt(editor.PointToScreen(new Point(caret.X + 1, caret.Y + 2)), editor.Handle);
        await WaitFor(() => editor.Text == committed && !CandidateWindowVisible(), "Clicking inside the editor kept suggestions open.");

        await OpenSuggestions();
        editor.Select(0, 1);
        await WaitFor(() => editor.SelectionLength == 1 && !CandidateWindowVisible(), "Changing the host selection kept suggestions open.");

        await OpenSuggestions();
        editor.SelectedText = "测";
        await WaitFor(() => editor.Text == "甲你好测乙" && !CandidateWindowVisible(), "An external editor change kept suggestions open.");

        editor.Text = "甲乙";
        editor.Select(1, 0);
        await TypeCode(window, editor, "nihao");
        await WaitFor(CandidateWindowVisible, "No active spelling candidates for a mouse pick.");
        void ClickFirstCandidate()
        {
            var popup = CandidateWindow();
            if (!GetWindowRect(popup, out var bounds)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            // The first horizontal candidate is just inside the left padding, beside its selection label.
            ClickAt(new Point(bounds.Left + 30 * editor.DeviceDpi / 96, (bounds.Top + bounds.Bottom) / 2), popup);
        }
        ClickFirstCandidate();
        await WaitFor(() => editor.Text == committed && CandidateWindowVisible(),
            "Picking 你好 by mouse did not commit it and offer continuations.");
        ClickFirstCandidate();
        await WaitFor(() => editor.Text.StartsWith("甲你好", StringComparison.Ordinal) && editor.Text.EndsWith('乙')
            && editor.Text.Length > committed.Length && editor.Text[3..^1].All(c => c is >= '\u3400' and <= '\u9fff'),
            "Clicking a suggested word did not insert it.");
        await SendChord(window, editor, Keys.Escape);
        await WaitFor(() => !CandidateWindowVisible(), "The candidate list remained after Escape.");
        async Task OpenWeather()
        {
            editor.Clear();
            await TypeCode(window, editor, "tianqi");
            await SendChord(window, editor, Keys.Space);
            await WaitFor(() => editor.Text == "天气" && CandidateWindowVisible(),
                "The installed public lexicon did not offer a weather continuation.");
        }

        await OpenWeather();
        ClickFirstCandidate();
        await WaitFor(() => editor.Text == "天气预报", "The first public continuation after 天气 did not insert 预报.");
        await SendChord(window, editor, Keys.Escape);
        await OpenWeather();
        await SendChord(window, editor, Keys.D1);
        await WaitFor(() => editor.Text == "天气预报", "Top-row 1 did not pick the first visible continuation.");
        await SendChord(window, editor, Keys.Escape);
        bool turnOnNumLock = !Control.IsKeyLocked(Keys.NumLock);
        try
        {
            if (turnOnNumLock) await SendChord(window, editor, Keys.NumLock);
            await OpenWeather();
            await SendChord(window, editor, Keys.NumPad2);
            await WaitFor(() => editor.Text == "天气怎么样", "Numpad 2 did not pick the second visible continuation.");
            await SendChord(window, editor, Keys.Escape);
        }
        finally
        {
            if (turnOnNumLock && Control.IsKeyLocked(Keys.NumLock)) await SendChord(window, editor, Keys.NumLock);
        }
        await OpenWeather();
        // Each page has at most seven numbered entries. No 8 label can be offered.
        await SendChord(window, editor, Keys.D8);
        await WaitFor(() => editor.Text == "天气8" && !CandidateWindowVisible(), "An unlabelled digit was swallowed.");
        await SendChord(window, editor, Keys.D2);
        await WaitFor(() => editor.Text == "天气82", "A digit without a prediction was intercepted.");
        await OpenWeather();
        await SendChord(window, editor, Keys.ControlKey, Keys.D2);
        await WaitFor(() => editor.Text == "天气" && !CandidateWindowVisible(), "Ctrl+2 selected a prediction.");
        editor.Clear();
        Console.WriteLine("Verified numbered prediction picks from both digit rows, unmatched and ordinary digits, and Ctrl+2.");
        Console.WriteLine("Verified that passive suggestions yield to Space, editor clicks and selection changes, and accept candidate clicks.");
    }

    private static void ClickAt(Point point, nint expectedWindow)
    {
        Cursor.Position = point;
        if (WindowFromPoint(point) != expectedWindow) throw new InvalidOperationException("The target of the isolated test click changed.");
        var click = new Input[2];
        click[0].Data.Mouse.Flags = 2;
        click[1].Data.Mouse.Flags = 4;
        if (SendInput(2, click, Marshal.SizeOf<Input>()) != 2) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static bool CandidateWindowVisible() => CandidateWindow() != 0;

    private static nint CandidateWindow()
    {
        // The installed TSF creates its non-activating tool popup on this editor's UI thread.
        const long candidateStyles = 0x08000000 | 0x00000080 | 0x00000008; // NOACTIVATE | TOOLWINDOW | TOPMOST
        nint found = 0;
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && (GetWindowLongPtr(hwnd, -20).ToInt64() & candidateStyles) == candidateStyles) found = hwnd;
            return found == 0;
        }, 0);
        return found;
    }

    private static async Task AssertEnglishCorrection(Form window, RichTextBox editor, string output)
    {
        Marshal.ThrowExceptionForHR(TF_CreateLangBarItemMgr(out var languageBar));
        var inputMode = new Guid("2c77a81e-41cc-4178-a3a7-5f8a987568e6");
        Marshal.ThrowExceptionForHR(languageBar.GetItem(ref inputMode, out object? item));
        if (item is not ITfLangBarItemButton mode)
            throw new InvalidOperationException("The language bar did not provide an input mode button.");
        Marshal.ThrowExceptionForHR(mode.GetText(out string label));
        if (label != "双拼互联")
            throw new InvalidOperationException($"Unexpected language bar mode button: {label}.");

        using var options = Registry.CurrentUser.CreateSubKey(@"Software\ShuangpinLink\IME");
        object? previous = options.GetValue("EnglishAutocorrect");
        var area = Rectangle.Empty;
        const int leftClick = 2;
        bool switched = false;
        try
        {
            options.SetValue("EnglishAutocorrect", 1);
            Environment.SetEnvironmentVariable("SHUANGPIN_LINK_INPUT_TEST_TRACE", "1");
            Marshal.ThrowExceptionForHR(mode.OnClick(leftClick, Point.Empty, ref area));
            switched = true;
            await Task.Delay(300);
            foreach (var key in new[] { Keys.T, Keys.E, Keys.H })
            {
                await SendChord(window, editor, key);
                await Task.Delay(70);
            }
            await WaitFor(() => editor.Text == "teh", "The editor did not receive the English literal before Space.");
            await SendChord(window, editor, Keys.Space);
            try { await WaitFor(() => editor.Text == "the ", "The native English corrector did not replace teh on Space."); }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException($"English editor text after Space: [{editor.Text}]", error);
            }
            SaveScreen(window, output, "english-corrected");
            Console.WriteLine("English mode: teh + Space committed the corrected word in an independent editor.");
            await SendChord(window, editor, Keys.ShiftKey, Keys.D2);
            await Task.Delay(180);
            await SendChord(window, editor, Keys.G);
            await Task.Delay(180);
            await SendChord(window, editor, Keys.Tab);
            await WaitFor(() => editor.Text == "the @gmail.com", "The English email candidate did not complete @g.");
        }
        finally
        {
            if (previous is null) options.DeleteValue("EnglishAutocorrect", throwOnMissingValue: false);
            else options.SetValue("EnglishAutocorrect", previous);
            Environment.SetEnvironmentVariable("SHUANGPIN_LINK_INPUT_TEST_TRACE", null);
            if (switched) mode.OnClick(leftClick, Point.Empty, ref area);
            Marshal.ReleaseComObject(mode);
            Marshal.ReleaseComObject(languageBar);
            editor.Clear();
        }
    }

    private static bool LoadedModule(string path)
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>()
            .Any(module => Path.GetFullPath(module.FileName).Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitFor(Func<bool> ready, string error)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(12))
                throw new InvalidOperationException(error);
            await Task.Delay(150);
        }
    }

    private static void SaveScreen(Form window, string output, string name)
    {
        var bounds = window.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
    }

    private static void DescribeDesktops()
    {
        const uint DesktopReadObjects = 0x0001;
        var inputDesktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (inputDesktop == IntPtr.Zero)
        {
            Console.WriteLine($"Input desktop is unavailable (Win32 error {Marshal.GetLastPInvokeError()}).");
            return;
        }
        try
        {
            Console.WriteLine($"Editor desktop={DesktopName(GetThreadDesktop(GetCurrentThreadId()))}, input desktop={DesktopName(inputDesktop)}.");
        }
        finally
        {
            CloseDesktop(inputDesktop);
        }
    }

    private static string DesktopName(IntPtr desktop)
    {
        const int Name = 2; // UOI_NAME
        var buffer = new StringBuilder(256);
        return GetUserObjectInformation(desktop, Name, buffer, (uint)(buffer.Capacity * sizeof(char)), out _)
            ? buffer.ToString() : $"unavailable ({Marshal.GetLastPInvokeError()})";
    }

    private static async Task EnsureFocus(Form window, Control field)
    {
        bool Ready() => GetForegroundWindow() == window.Handle && field.Focused;
        if (Ready()) return;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint previousForeground);
        Console.WriteLine($"Acquiring editor focus from PID {previousForeground}.");
        // Setup starts the settings window asynchronously. It can become foreground after the editor is shown.
        // A real click in the editor form's empty padding can activate it even under the foreground lock.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            window.Activate();
            field.Focus();
            await Task.Delay(100);
            if (!Ready())
            {
                bool topmost = window.TopMost;
                try
                {
                    window.TopMost = true;
                    Point pad = window.PointToScreen(new Point(8, 8));
                    Cursor.Position = pad;
                    if (WindowFromPoint(pad) == window.Handle)
                    {
                        var click = new Input[2];
                        click[0].Data.Mouse.Flags = 0x0002; // MOUSEEVENTF_LEFTDOWN
                        click[1].Data.Mouse.Flags = 0x0004; // MOUSEEVENTF_LEFTUP
                        if (SendInput(2, click, Marshal.SizeOf<Input>()) != 2)
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not activate the isolated editor.");
                        await Task.Delay(100);
                    }
                    field.Focus();
                }
                finally { window.TopMost = topmost; }
            }
            await Task.Delay(150);
            if (Ready()) return;
        }
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        throw new InvalidOperationException($"Could not focus the isolated editor. Foreground PID {foreground}, editor PID {Environment.ProcessId}.");
    }

    private static async Task SendChord(Form window, Control field, params Keys[] keys)
    {
        await EnsureFocus(window, field);
        var inputs = new Input[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = KeyInput(keys[i], false);
            inputs[inputs.Length - i - 1] = KeyInput(keys[i], true);
        }
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not send keyboard input to the isolated editor.");
    }

    private static Input KeyInput(Keys key, bool release, bool extended = false) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Data = new InputData { Keyboard = new KeyboardInput { Key = (ushort)key,
            Flags = (release ? 2u : 0u) | (extended || key is Keys.Left or Keys.Right ? 1u : 0u) } },
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private delegate bool EnumWindow(nint hwnd, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindow callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hwnd, out WindowBounds bounds);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr desktop, int index, StringBuilder name, uint length, out uint required);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("input.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool InstallLayoutOrTip(string profiles, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputProfile
    {
        public uint Type;
        public ushort Language;
        public Guid ClassId, ProfileId, Category;
        public IntPtr SubstituteLayout;
        public uint Capabilities;
        public IntPtr Layout;
        public uint Flags;
    }

    [DllImport("msctf.dll")]
    private static extern int TF_CreateLangBarItemMgr(out ITfLangBarItemMgr manager);

    [ComImport, Guid("ba468c55-9956-4fb1-a59d-52a7dd7cc6aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfLangBarItemMgr
    {
        [PreserveSig] int EnumItems(out IntPtr enumerator);
        [PreserveSig] int GetItem(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object? item);
    }

    [ComImport, Guid("28c7f1d0-de25-11d2-afdd-00105a2799b5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfLangBarItemButton
    {
        [PreserveSig] int GetInfo(IntPtr info);
        [PreserveSig] int GetStatus(out uint status);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
        [PreserveSig] int GetTooltipString([MarshalAs(UnmanagedType.BStr)] out string tooltip);
        [PreserveSig] int OnClick(int click, Point point, ref Rectangle area);
        [PreserveSig] int InitMenu(IntPtr menu);
        [PreserveSig] int OnMenuSelect(uint item);
        [PreserveSig] int GetIcon(out IntPtr icon);
        [PreserveSig] int GetText([MarshalAs(UnmanagedType.BStr)] out string text);
    }

    [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileManager
    {
        [PreserveSig] int ActivateProfile(uint type, ushort language, in Guid clsid, in Guid profile, IntPtr layout, uint flags);
        // Preserve the COM vtable positions of the unused SDK methods.
        void _VtblGap1_4();
        [PreserveSig] int RegisterProfile(in Guid clsid, ushort language, in Guid profile,
            [MarshalAs(UnmanagedType.LPWStr)] string description, uint descriptionLength,
            [MarshalAs(UnmanagedType.LPWStr)] string iconFile, uint iconFileLength, uint iconIndex,
            IntPtr substituteLayout, uint preferredLayout, [MarshalAs(UnmanagedType.Bool)] bool enabledByDefault, uint flags);
        void _VtblGap2_1();
        [PreserveSig] int GetActiveProfile(in Guid category, out InputProfile profile);
    }

    [ComImport, Guid("c3acefb5-f69d-4905-938f-fcadcf4be830"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfCategoryManager
    {
        [PreserveSig] int RegisterCategory(in Guid clsid, in Guid category, in Guid item);
    }

    [ComImport, Guid("1f02b6c5-7842-4ee6-8a0b-9a24183a95ca"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfiles
    {
        void _VtblGap1_9();
        [PreserveSig] int GetLanguageProfileDescription(in Guid clsid, ushort language, in Guid profile,
            [MarshalAs(UnmanagedType.BStr)] out string description);
        void _VtblGap2_5();
        [PreserveSig] int IsEnabledLanguageProfile(in Guid clsid, ushort language, in Guid profile,
            [MarshalAs(UnmanagedType.Bool)] out bool enabled);
    }
}
