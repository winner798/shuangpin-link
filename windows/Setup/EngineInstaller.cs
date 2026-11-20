using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using ShuangpinLink;

static class EngineInstaller
{
    internal static readonly string[] RequiredFiles =
    [
        InputPaths.Server, InputPaths.Deployer, InputPaths.Service64, InputPaths.Service32, "rime.dll",
        "data/default.yaml", "data/shuangpin_link.yaml", "data/predict.db",
        "data/double_pinyin_mspy.schema.yaml", "data/rime_ice.schema.yaml"
    ];

    const string LegacyIds = "{A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A}{3D02CAB6-2B8E-4781-BA20-1C9267529467}";
    const uint RemoveInputProfile = 0x00000001;

    [DllImport("input.dll", EntryPoint = "InstallLayoutOrTip", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InstallLayoutOrTip(string profiles, uint flags);

    static string SetupExecutable => Environment.ProcessPath ?? throw new FileNotFoundException("无法找到双拼互联安装程序。");

    static string Version
    {
        get
        {
            string? text = FileVersionInfo.GetVersionInfo(SetupExecutable).FileVersion;
            if (!System.Version.TryParse(text, out var version)) throw new InvalidDataException("安装程序版本无效。");
            return version.ToString();
        }
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static string EnsureInstalled(ZipArchive archive)
    {
        if (InstalledCurrent() is { } current) return current;
        if (IsElevated()) InstallMachineFiles(archive);
        else
        {
            // The elevated worker reads the installer embedded in its own executable.
            var launch = new ProcessStartInfo(SetupExecutable, "--install-engine") { UseShellExecute = true, Verb = "runas" };
            using var worker = Process.Start(launch) ?? throw new InvalidOperationException("无法启动系统输入组件安装。");
            worker.WaitForExit();
            if (worker.ExitCode != 0) throw new InvalidOperationException("系统输入组件安装失败。");
        }
        return InstalledCurrent() ?? throw new InvalidOperationException("系统输入组件注册失败。");
    }

    static string? InstalledCurrent()
    {
        string? directory = InputPaths.InstalledEngine();
        if (directory is null || !RequiredFiles.All(name => File.Exists(Path.Combine(directory, name)))) return null;
        string marker = Path.Combine(directory, "engine-version.txt");
        if (!File.Exists(marker) || File.ReadAllText(marker) != Version) return null;
        return IsRegistered(directory, is64: true) && IsRegistered(directory, is64: false) ? directory : null;
    }

    internal static void InstallMachineFiles(ZipArchive archive)
    {
        if (!IsElevated()) throw new UnauthorizedAccessException("安装系统输入组件需要管理员权限。");
        using var installLock = new Mutex(false, @"Global\ShuangpinLink.EngineInstaller");
        try
        {
            try
            {
                if (!installLock.WaitOne(TimeSpan.FromMinutes(1))) throw new IOException("系统输入组件正在安装，请稍后重试。");
            }
            catch (AbandonedMutexException) { }
            if (InstalledCurrent() is not null) return;
            var previous = InputPaths.InstalledEngine();
            var destination = Path.Combine(InputPaths.EngineRoot, Version + "-" + Guid.NewGuid().ToString("N")[..8]);
            ExtractEngine(archive, destination);
            File.WriteAllText(Path.Combine(destination, "engine-version.txt"), Version);
            try
            {
                Register(destination, is64: false);
                Register(destination, is64: true);
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = machine.CreateSubKey(InputPaths.EngineKey);
                key.SetValue("InstallDir", destination);
            }
            catch
            {
                if (previous is null)
                {
                    TryRegister(destination, is64: false, unregister: true);
                    TryRegister(destination, is64: true, unregister: true);
                }
                else
                {
                    TryRegister(previous, is64: false, unregister: false);
                    TryRegister(previous, is64: true, unregister: false);
                }
                throw;
            }
        }
        finally
        {
            try { installLock.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
    }

    static void ExtractEngine(ZipArchive archive, string directory)
    {
        // Extract the privileged payload directly into Program Files, without staging DLLs in a writable temp folder.
        const string prefix = "ime/";
        string root = directory + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries.Where(file => file.FullName.StartsWith(prefix, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(file.Name)))
        {
            string target = Path.GetFullPath(Path.Combine(directory, entry.FullName[prefix.Length..]));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("系统输入组件路径无效。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
    }

    static bool IsRegistered(string directory, bool is64)
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, is64 ? RegistryView.Registry64 : RegistryView.Registry32);
        using var entry = registry.OpenSubKey($@"SOFTWARE\Classes\CLSID\{InputPaths.ClassId}\InprocServer32");
        return entry?.GetValue(null) is string path && StringComparer.OrdinalIgnoreCase.Equals(path,
            Path.Combine(directory, is64 ? InputPaths.Service64 : InputPaths.Service32));
    }

    static void Register(string directory, bool is64, bool unregister = false)
    {
        string osDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), is64 ? "System32" : "SysWOW64");
        var info = new ProcessStartInfo(Path.Combine(osDirectory, "regsvr32.exe")) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("/s");
        if (unregister) info.ArgumentList.Add("/u");
        info.ArgumentList.Add(Path.Combine(directory, is64 ? InputPaths.Service64 : InputPaths.Service32));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Windows 输入组件注册工具。");
        process.WaitForExit();
        if (process.ExitCode != 0 || (!unregister && !IsRegistered(directory, is64)))
            throw new InvalidOperationException($"Windows 输入组件注册失败（{(is64 ? "64" : "32")} 位，错误 {process.ExitCode}）。");
    }

    static void TryRegister(string directory, bool is64, bool unregister)
    {
        try { Register(directory, is64, unregister); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal static void EnableCurrentUser(bool hideLegacy)
    {
        if (!InstallLayoutOrTip("0x0804:" + InputPaths.ClassId + InputPaths.ProfileId, 0))
            throw new InvalidOperationException("无法在当前用户的 Windows 输入法列表中启用双拼互联。");
        if (hideLegacy)
        {
            InstallLayoutOrTip("0x0804:" + LegacyIds, RemoveInputProfile);
            InstallLayoutOrTip("0x0404:" + LegacyIds, RemoveInputProfile);
        }
    }
}
