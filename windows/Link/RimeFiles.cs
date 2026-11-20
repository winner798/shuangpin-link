using System.Text;

namespace ShuangpinLink;

internal static class RimeFiles
{
    internal const string DictionaryFile = "rime_ice.userdb.txt";
    static string UserDirectory => InputPaths.UserDirectory;
    internal static string RestoreDirectory => Path.Combine(UserDirectory, "sync", "shuangpin-link-peer");
    internal static bool LearningEnabled => LearningSettings.Enabled(UserDirectory);
    static string FuzzyPath => Path.Combine(UserDirectory, "link_profile.custom.yaml");
    internal static bool FuzzyEnabled => File.Exists(FuzzyPath);

    internal static async Task SetFuzzy(bool enabled, CancellationToken token)
    {
        string path = FuzzyPath;
        const string patch = """
            patch:
              double_pinyin_mspy:
                __include: link_spellings:/double_pinyin_mspy_fuzzy
              rime_ice:
                __include: link_spellings:/rime_ice_fuzzy
            """;
        Directory.CreateDirectory(UserDirectory);
        byte[]? before = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
        if (enabled) await WriteAtomic(path, Encoding.UTF8.GetBytes(patch + "\n"), token);
        else File.Delete(path);
        try { await RunDeployer("/deploy", token); }
        catch
        {
            if (before is null) File.Delete(path);
            else await WriteAtomic(path, before, CancellationToken.None);
            await RunDeployer("/deploy", CancellationToken.None);
            throw;
        }
    }
    internal static async Task RunDeployer(string option, CancellationToken token)
    {
        using var process = InputPaths.StartTool(InputPaths.RequireEngine(), InputPaths.Deployer, option);
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
        string root = InputPaths.RequireEngine();
        await InputPaths.StopServer(root, token);
        try
        {
            string backup = Path.Combine(UserDirectory, "shuangpin-link-cleared", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backup);
            var directory = Path.Combine(UserDirectory, "rime_ice.userdb");
            if (Directory.Exists(directory)) Directory.Move(directory, Path.Combine(backup, "rime_ice.userdb"));
            var sync = Path.Combine(UserDirectory, "sync");
            if (Directory.Exists(sync))
            {
                foreach (string folder in Directory.EnumerateDirectories(sync))
                {
                    string file = Path.Combine(folder, DictionaryFile);
                    if (!File.Exists(file)) continue;
                    string destination = Path.Combine(backup, "sync", Path.GetFileName(folder));
                    Directory.CreateDirectory(destination);
                    File.Move(file, Path.Combine(destination, DictionaryFile));
                }
            }
        }
        finally
        {
            InputPaths.StartTool(root, InputPaths.Server).Dispose();
        }
    }

    internal static async Task SetLearning(bool enabled, CancellationToken token)
    {
        var directory = UserDirectory;
        Directory.CreateDirectory(directory);
        List<(string Path, byte[]? Before, byte[] After)> edits = [];
        foreach (var name in LearningSettings.SchemaFiles)
        {
            string path = Path.Combine(directory, name);
            byte[]? before = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
            using var text = before is null ? null : new StreamReader(new MemoryStream(before));
            string after = LearningSettings.WithLearning(text?.ReadToEnd(), enabled);
            edits.Add((path, before, Encoding.UTF8.GetBytes(after)));
        }

        int written = 0;
        try
        {
            foreach (var edit in edits)
            {
                await WriteAtomic(edit.Path, edit.After, token);
                written++;
            }
            await RunDeployer("/deploy", token);
        }
        catch
        {
            foreach (var edit in edits.Take(written))
            {
                if (edit.Before is null) File.Delete(edit.Path);
                else await WriteAtomic(edit.Path, edit.Before, CancellationToken.None);
            }
            if (written == edits.Count) await RunDeployer("/deploy", CancellationToken.None);
            throw;
        }
    }

    static async Task WriteAtomic(string path, byte[] contents, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, contents, token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
