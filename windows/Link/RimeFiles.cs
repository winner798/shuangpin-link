using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace ShuangpinLink;

internal static class RimeFiles
{
    internal const string DictionaryFile = "rime_ice.userdb.txt";
    static string UserDirectory => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Rime\Weasel", "RimeUserDir", null) as string
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rime");
    internal static string RestoreDirectory => Path.Combine(UserDirectory, "sync", "shuangpin-link-peer");

    internal static async Task RunDeployer(string option, CancellationToken token)
    {
        var path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rime\Weasel", "InstallDir", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Rime\Weasel", "InstallDir", null) as string;
        if (path is null) throw new FileNotFoundException("未找到小狼毫输入法。");
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(path, "WeaselDeployer.exe"), option)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("无法启动输入法设置。");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException("输入法更新失败。");
    }

    internal static async Task<byte[]?> Export(CancellationToken token)
    {
        await RunDeployer("/sync", token);
        var installation = File.ReadLines(Path.Combine(UserDirectory, "installation.yaml"))
            .First(line => line.StartsWith("installation_id:", StringComparison.Ordinal))
            .Split(':', 2)[1].Trim().Trim('"', '\'');
        if (installation.Length == 0 || installation.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || installation is "." or "..") throw new InvalidDataException("输入法目录信息损坏。");
        string file = Path.Combine(UserDirectory, "sync", installation, DictionaryFile);
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("个人词库超过 8 MB。");
        return await File.ReadAllBytesAsync(file, token);
    }

    internal static async Task Import(byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(RestoreDirectory);
        var file = Path.Combine(RestoreDirectory, DictionaryFile);
        await File.WriteAllBytesAsync(file + ".tmp", bytes, token);
        File.Move(file + ".tmp", file, overwrite: true);
        try { await RunDeployer("/sync", token); }
        finally { File.Delete(file); }
    }

    internal static async Task ClearDictionary(CancellationToken token)
    {
        // Rime's dictionary manager performs deletion while its engine is in maintenance mode.
        await RunDeployer("/dict", token);
    }

    internal static async Task SetLearning(bool enabled, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(UserDirectory, "double_pinyin_mspy.custom.yaml"),
            $"patch:\n  translator/enable_user_dict: {enabled.ToString().ToLower(CultureInfo.InvariantCulture)}\n", token);
        await RunDeployer("/deploy", token);
    }
}
