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
        if (args is ["--check-history", var folder])
        {
            HistoryFixture.Run(folder).GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--seed-legacy-tip"] or ["--assert-legacy-tip-hidden"])
            return LegacyTipFixture(seed: args[0] == "--seed-legacy-tip");
        if (args.Length != 2)
            throw new ArgumentException("Usage: Editor <screenshot-directory> <installed-TSF-DLL>");

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
                window.Activate();
                editor.Focus();
                await WaitFor(() => GetForegroundWindow() == window.Handle && editor.Focused,
                    "The isolated Windows session did not give focus to the test editor.");
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

                    await AssertCommit(window, editor, args[0], "nihk", "microsoft-double-pinyin");

                    SendChord(Keys.ControlKey, Keys.Oemtilde);
                    await Task.Delay(600);
                    SaveScreen(window, args[0], "input-scheme-picker");
                    // In the Rime scheme picker, pressing the hotkey again advances to the next scheme.
                    SendChord(Keys.ControlKey, Keys.Oemtilde);
                    await Task.Delay(180);
                    SendChord(Keys.Enter);
                    await Task.Delay(550);

                    await AssertCommit(window, editor, args[0], "nihao", "full-pinyin");
                    await AssertEnglishCorrection(window, editor, args[0]);
                    using (var masked = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true })
                    {
                        window.Controls.Add(masked);
                        masked.BringToFront();
                        masked.Focus();
                        await WaitFor(() => masked.Focused, "The disposable password field did not get focus.");
                        foreach (var key in new[] { Keys.N, Keys.I, Keys.H, Keys.A, Keys.O, Keys.Space })
                        {
                            SendChord(key);
                            await Task.Delay(80);
                        }
                        await WaitFor(() => masked.Text == "nihao ", "A password field did not receive literal keys.");
                        editor.Focus();
                    }
                    await HistoryFixture.Run(args[0]);
                    await AssertClipboardPrivacy(window.Handle);
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

    private static async Task AssertCommit(Form window, RichTextBox editor, string output, string keys, string name)
    {
        if (editor.Text.Length != 0)
            throw new InvalidOperationException($"The editor was not empty before {name}: [{editor.Text}]");
        if (GetForegroundWindow() != window.Handle || !editor.Focused)
            throw new InvalidOperationException("The test editor lost keyboard focus.");

        foreach (char key in keys)
        {
            SendChord((Keys)char.ToUpperInvariant(key));
            await Task.Delay(110);
        }
        await Task.Delay(430);
        SaveScreen(window, output, name + "-candidates");
        SendChord(Keys.Space);
        await WaitFor(() => editor.Text == "你好", $"{name} did not commit 你好 after typing {keys}.");
        SendChord(Keys.Escape);
        await Task.Delay(250);
        if (editor.Text != "你好")
            throw new InvalidOperationException($"{name} composition did not remain committed after Escape.");
        SaveScreen(window, output, name + "-committed");
        Console.WriteLine($"{name}: {keys} + Space committed {editor.Text} in an independent editor.");
        editor.Clear();
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
                SendChord(key);
                await Task.Delay(70);
            }
            await WaitFor(() => editor.Text == "teh", "The editor did not receive the English literal before Space.");
            SendChord(Keys.Space);
            try { await WaitFor(() => editor.Text == "the ", "The native English corrector did not replace teh on Space."); }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException($"English editor text after Space: [{editor.Text}]", error);
            }
            SaveScreen(window, output, "english-corrected");
            Console.WriteLine("English mode: teh + Space committed the corrected word in an independent editor.");
            SendChord(Keys.ShiftKey, Keys.D2);
            await Task.Delay(180);
            SendChord(Keys.G);
            await Task.Delay(180);
            SendChord(Keys.Tab);
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

    private static void SendChord(params Keys[] keys)
    {
        var inputs = new Input[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = KeyInput(keys[i], false);
            inputs[inputs.Length - i - 1] = KeyInput(keys[i], true);
        }
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not send keyboard input to the isolated editor.");
    }

    private static Input KeyInput(Keys key, bool release) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Data = new InputData { Keyboard = new KeyboardInput { Key = (ushort)key, Flags = release ? 2u : 0u } },
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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
