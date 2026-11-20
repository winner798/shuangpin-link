using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;
using ShuangpinLink;

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
        bool resumeFromUpdate = false;
        bool complete = false;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("install.zip")
                ?? throw new InvalidDataException("安装文件不存在。");
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var required = new[] { "weasel.exe", "ShuangpinLink.exe", "rime/default.custom.yaml", "rime/predict.db", "rime/double_pinyin_mspy.schema.yaml", "rime/rime_ice.schema.yaml" };
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
            resumeFromUpdate = WaitForPreviousInstance(args);
            if (MessageBox.Show("将安装双拼互联，包含微软双拼、普通拼音和设备互通。现有输入配置将先备份。继续安装？", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return 0;
            zip.ExtractToDirectory(stage);
            Install(stage);
            complete = true;
            MessageBox.Show("安装完成。按 Win + 空格选择「小狼毫」。打字时按 Ctrl + ` 切换微软双拼和普通拼音。", Title,
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
            if (resumeFromUpdate && !complete)
            {
                try { StartInstalledApplication(); }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    static bool WaitForPreviousInstance(string[] args)
    {
        int flag = Array.IndexOf(args, "--wait-for");
        if (flag < 0) return false;
        if (flag + 1 >= args.Length || !int.TryParse(args[flag + 1], out int processId)
            || processId <= 0 || processId == Environment.ProcessId)
            throw new ArgumentException("无法取得原程序的进程信息。");

        Process previous;
        try { previous = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; }
        using (previous)
        {
            if (!previous.WaitForExit(30000))
                throw new InvalidOperationException("双拼互联仍在运行。请从托盘退出后重新安装。");
        }
        return true;
    }

    static string? WeaselLocation() =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rime\Weasel", "InstallDir", null) as string
        ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Rime\Weasel", "InstallDir", null) as string;

    static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShuangpinLink");
    static string InstalledExecutable => Path.Combine(LocalRoot, "app", "ShuangpinLink.exe");

    static void StartInstalledApplication()
    {
        if (File.Exists(InstalledExecutable))
            Process.Start(new ProcessStartInfo(InstalledExecutable) { UseShellExecute = true })?.Dispose();
    }

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
        var executable = InstalledExecutable;
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        var stagedExecutable = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = Path.Combine(LocalRoot, "configuration-backup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var source = Path.Combine(stage, "rime");
        var updated = new List<(string Destination, string? Backup)>();
        try
        {
            if (!LearningSettings.Enabled(rimeUser))
            {
                foreach (var name in LearningSettings.SchemaFiles)
                {
                    string previous = Path.Combine(rimeUser, name);
                    var contents = File.Exists(previous) ? File.ReadAllText(previous) : null;
                    File.WriteAllText(Path.Combine(source, name), LearningSettings.WithLearning(contents, enabled: false));
                }
            }
            File.Copy(Path.Combine(stage, "ShuangpinLink.exe"), stagedExecutable);
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
            File.Move(stagedExecutable, executable, overwrite: true);
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
        finally
        {
            try { File.Delete(stagedExecutable); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        using var autostart = Registry.CurrentUser.CreateSubKey(RunKey);
        autostart.SetValue("ShuangpinLink", $"\"{executable}\" --tray");
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        File.WriteAllText(Path.Combine(programs, "双拼互联.url"), "[InternetShortcut]\r\nURL=" + new Uri(executable).AbsoluteUri + "\r\n");
        StartInstalledApplication();
    }

    static void Deploy(string root)
    {
        using var deployer = Process.Start(new ProcessStartInfo(Path.Combine(root, "WeaselDeployer.exe"), "/deploy") { UseShellExecute = false })
            ?? throw new InvalidOperationException("无法部署输入法配置。");
        deployer.WaitForExit();
        if (deployer.ExitCode != 0) throw new InvalidOperationException("输入方案部署失败，配置已恢复。");
    }
}
