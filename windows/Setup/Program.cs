using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

static class Setup
{
    const string Title = "双拼互联安装";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string stage = Path.Combine(Path.GetTempPath(), "ShuangpinLink-" + Guid.NewGuid().ToString("N"));
        bool verify = args.Contains("--check");
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("install.zip")
                ?? throw new InvalidDataException("安装文件不存在。");
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var required = new[] { "weasel.exe", "ShuangpinLink.exe", "rime/default.custom.yaml", "rime/predict.db", "rime/double_pinyin_mspy.schema.yaml" };
            if (required.Any(file => zip.GetEntry(file)?.Length is null or 0))
                throw new InvalidDataException("安装包不完整。");
            if (verify)
            {
                foreach (var entry in zip.Entries)
                {
                    using var contents = entry.Open();
                    contents.CopyTo(Stream.Null);
                }
                return 0;
            }
            if (MessageBox.Show("将安装微软双拼输入法和托盘同步程序。若已安装小狼毫，会先备份将被替换的配置和词库文件。继续吗？", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return 0;
            zip.ExtractToDirectory(stage);
            Install(stage);
            MessageBox.Show("安装完成。按 Win + 空格选择小狼毫，点击托盘中的双拼互联打开配对设置。", Title,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            if (!verify) MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    static string? WeaselLocation() =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rime\Weasel", "InstallDir", null) as string
        ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Rime\Weasel", "InstallDir", null) as string;

    static void Install(string stage)
    {
        if (WeaselLocation() is not { } existing || !File.Exists(Path.Combine(existing, "WeaselDeployer.exe")))
        {
            using var installer = Process.Start(new ProcessStartInfo(Path.Combine(stage, "weasel.exe")) { UseShellExecute = true })
                ?? throw new InvalidOperationException("无法启动小狼毫安装程序。");
            installer.WaitForExit();
            if (installer.ExitCode != 0) throw new InvalidOperationException("小狼毫安装未完成。");
        }
        var weasel = WeaselLocation() ?? throw new InvalidOperationException("小狼毫安装未完成。");
        var rimeUser = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Rime\Weasel", "RimeUserDir", null) as string
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rime");
        var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShuangpinLink");
        var app = Path.Combine(localRoot, "app");
        Directory.CreateDirectory(app);
        var executable = Path.Combine(app, "ShuangpinLink.exe");
        File.Copy(Path.Combine(stage, "ShuangpinLink.exe"), executable, overwrite: true);
        var backup = Path.Combine(localRoot, "configuration-backup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var source = Path.Combine(stage, "rime");
        var updated = new List<(string Destination, string? Backup)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(rimeUser, relative);
                string? saved = null;
                if (File.Exists(destination))
                {
                    saved = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                updated.Add((destination, saved));
                File.Copy(file, destination, overwrite: true);
            }
            Deploy(weasel);
        }
        catch
        {
            foreach (var entry in updated)
            {
                if (entry.Backup is null) File.Delete(entry.Destination);
                else File.Copy(entry.Backup, entry.Destination, overwrite: true);
            }
            throw;
        }

        using var autostart = Registry.CurrentUser.CreateSubKey(RunKey);
        autostart.SetValue("ShuangpinLink", $"\"{executable}\" --tray");
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        File.WriteAllText(Path.Combine(programs, "双拼互联.url"), "[InternetShortcut]\r\nURL=" + new Uri(executable).AbsoluteUri + "\r\n");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }

    static void Deploy(string root)
    {
        using var deployer = Process.Start(new ProcessStartInfo(Path.Combine(root, "WeaselDeployer.exe"), "/deploy") { UseShellExecute = false })
            ?? throw new InvalidOperationException("无法部署输入法配置。");
        deployer.WaitForExit();
        if (deployer.ExitCode != 0) throw new InvalidOperationException("微软双拼词库部署失败，配置已恢复。");
    }
}
