using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
        string? stage = null;
        bool verify = args.Contains("--check");
        bool engineOnly = args.Contains("--install-engine");
        bool quiet = verify || engineOnly || args.Contains("--quiet");
        bool resumeFromUpdate = false;
        bool complete = false;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("install.zip")
                ?? throw new InvalidDataException("安装文件不存在。");
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            string[] required = ["ShuangpinLink.exe", "rime/default.custom.yaml", "rime/shuangpin_link.custom.yaml",
                .. EngineInstaller.RequiredFiles.Select(file => "ime/" + file)];
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
            if (RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("此安装包适用于 x64 Windows。");
            if (!engineOnly)
            {
                resumeFromUpdate = WaitForPreviousInstance(args);
                if (!quiet && MessageBox.Show("将安装双拼互联，包含微软双拼、普通拼音和设备互通。安装系统输入组件时，Windows 可能请求管理员权限。继续安装？", Title,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return 0;
            }
            if (engineOnly)
            {
                EngineInstaller.InstallMachineFiles(zip);
                return 0;
            }
            using var userInstaller = new Mutex(true, @"Local\ShuangpinLink.UserSetup", out bool firstInstaller);
            if (!firstInstaller) throw new InvalidOperationException("当前用户的双拼互联正在安装。");
            stage = Directory.CreateTempSubdirectory("ShuangpinLink-").FullName;
            foreach (string file in required.Where(file => !file.StartsWith("ime/", StringComparison.Ordinal)))
            {
                string destination = Path.Combine(stage, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                zip.GetEntry(file)!.ExtractToFile(destination);
            }
            string? migrationNotice = Install(zip, stage);
            complete = true;
            if (!quiet)
                MessageBox.Show("安装完成。按 Win + 空格选择「双拼互联」。打字时按 Ctrl + ` 切换微软双拼和普通拼音。" + migrationNotice, Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            if (quiet)
            {
                using var diagnostics = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
                diagnostics.WriteLine(error);
            }
            else MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (resumeFromUpdate && !complete)
            {
                try { StartInstalledApplication(); }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (stage is not null && Directory.Exists(stage))
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

    static string LocalRoot => InputPaths.ApplicationDirectory;
    static string InstalledExecutable => Path.Combine(LocalRoot, "app", "ShuangpinLink.exe");

    static void StartInstalledApplication()
    {
        if (File.Exists(InstalledExecutable))
            Process.Start(new ProcessStartInfo(InstalledExecutable) { UseShellExecute = true })?.Dispose();
    }

    static string? Install(ZipArchive archive, string stage)
    {
        var previous = InputPaths.InstalledEngine();
        var engine = EngineInstaller.EnsureInstalled(archive);
        var rimeUser = InputPaths.UserDirectory;
        var legacy = InputDataMigration.FindLegacyDirectory(LocalRoot);
        string migrationMarker = Path.Combine(rimeUser, "legacy-dictionary-migrated");
        var migration = legacy is not null && !File.Exists(migrationMarker)
            ? InputDataMigration.Import(legacy, rimeUser) : default;
        var executable = InstalledExecutable;
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        var stagedExecutable = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = Path.Combine(LocalRoot, "configuration-backup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var source = Path.Combine(stage, "rime");
        var updated = new List<(string Destination, string? Backup)>();
        bool stoppedPrevious = false;
        try
        {
            if (previous is not null)
            {
                InputPaths.StopServer(previous, CancellationToken.None).GetAwaiter().GetResult();
                stoppedPrevious = true;
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
            RunDeployer(engine, "/deploy");
            if (migration.SnapshotPath is { } snapshot)
            {
                RunDeployer(engine, "/restore", snapshot);
                File.Delete(snapshot);
            }
            if (legacy is not null && migration.Dictionary != LegacyDictionaryMigration.Unavailable)
                File.WriteAllText(migrationMarker, "1");
            EngineInstaller.EnableCurrentUser(hideLegacy: legacy is not null);
            File.Move(stagedExecutable, executable, overwrite: true);
        }
        catch
        {
            foreach (var entry in updated)
            {
                if (entry.Backup is null) File.Delete(entry.Destination);
                else File.Copy(entry.Backup, entry.Destination, overwrite: true);
            }
            if (stoppedPrevious)
            {
                try
                {
                    RunDeployer(engine, "/deploy");
                    InputPaths.StartTool(engine, InputPaths.Server).Dispose();
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
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
        InputPaths.StartTool(engine, InputPaths.Server).Dispose();
        StartInstalledApplication();
        return migration.Dictionary == LegacyDictionaryMigration.Unavailable
            ? "\n旧版词库正在使用，已保留原有文件。" : null;
    }

    static void RunDeployer(string root, params string[] arguments)
    {
        using var deployer = InputPaths.StartTool(root, InputPaths.Deployer, arguments);
        deployer.WaitForExit();
        if (deployer.ExitCode != 0) throw new InvalidOperationException($"输入方案部署失败（错误 {deployer.ExitCode}）。");
    }
}
