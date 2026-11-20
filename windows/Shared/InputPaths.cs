using System.Diagnostics;
using Microsoft.Win32;

namespace ShuangpinLink;

internal static class InputPaths
{
    internal const string EngineKey = @"SOFTWARE\ShuangpinLink\IME";
    internal const string ClassId = "{A6D4A312-7DF3-4BD0-9D84-BBD25BC60AA1}";
    internal const string ProfileId = "{40CFBB46-1EB1-41F3-A336-75CB9A137BD2}";
    internal const string Server = "ShuangpinImeServer.exe";
    internal const string Deployer = "ShuangpinImeDeployer.exe";
    internal const string Service64 = "ShuangpinIme64.dll";
    internal const string Service32 = "ShuangpinIme32.dll";

    internal static string ApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShuangpinLink");
    internal static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShuangpinLink", "Rime");
    internal static string EngineRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ShuangpinLink", "IME");

    internal static string? InstalledEngine()
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = registry.OpenSubKey(EngineKey);
        if (key?.GetValue("InstallDir") is not string path || !Path.IsPathFullyQualified(path)) return null;
        path = Path.GetFullPath(path);
        return StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), EngineRoot) ? path : null;
    }

    internal static string RequireEngine() => InstalledEngine()
        ?? throw new FileNotFoundException("未找到双拼互联系统输入组件，请重新运行安装程序。");

    internal static Process StartTool(string directory, string name, params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(directory, name))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动双拼互联的输入组件。");
    }

    internal static async Task StopServer(string directory, CancellationToken token)
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Server));
        try
        {
            if (!processes.Any(process => !process.HasExited && process.SessionId == current.SessionId)) return;
            using var quit = StartTool(directory, Server, "/q");
            await quit.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
            foreach (var process in processes)
            {
                if (!process.HasExited && process.SessionId == current.SessionId)
                    await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
