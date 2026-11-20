using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShuangpinLink;

internal enum LegacyDictionaryMigration
{
    None,
    DatabaseCopied,
    SnapshotStaged,
    Unavailable,
}

internal readonly record struct InputDataMigrationResult(LegacyDictionaryMigration Dictionary, string? SnapshotPath);

internal static partial class InputDataMigration
{
    internal const string DictionaryDirectory = "rime_ice.userdb";
    internal const string SnapshotFile = "rime_ice.userdb.txt";
    const long MaxSnapshotSize = 8 * 1024 * 1024;

    [GeneratedRegex(@"\AMANIFEST-[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestName();

    [GeneratedRegex(@"\A[0-9]+\.(?:ldb|sst|log)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DataFileName();

    internal static string? FindLegacyDirectory(string applicationDirectory)
    {
        if (!File.Exists(Path.Combine(applicationDirectory, "app", "ShuangpinLink.exe"))) return null;

        var configured = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Rime\Weasel", "RimeUserDir", null) as string;
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rime")
            : configured;
        if (!Path.IsPathFullyQualified(path)) return null;
        return IsPreviousProjectData(path) ? Path.GetFullPath(path) : null;
    }

    static bool IsPreviousProjectData(string path)
    {
        if (!File.Exists(Path.Combine(path, "predict.db"))
            || !File.Exists(Path.Combine(path, "double_pinyin_mspy.schema.yaml"))) return false;
        string palette = Path.Combine(path, "weasel.custom.yaml");
        try
        {
            return File.Exists(palette) && !IsReparsePoint(palette) && File.ReadLines(palette).Any(line =>
                line.TrimStart().Equals("preset_color_schemes/shuangpin_link:", StringComparison.Ordinal));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static InputDataMigrationResult Import(string legacyDirectory, string destination)
    {
        legacyDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyDirectory));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (StringComparer.OrdinalIgnoreCase.Equals(legacyDirectory, destination))
            throw new ArgumentException("旧版和新版必须使用不同的输入法数据目录。", nameof(destination));
        if (!Directory.Exists(legacyDirectory)) return default;

        Directory.CreateDirectory(destination);
        if (!LearningSettings.Enabled(legacyDirectory))
        {
            foreach (string name in LearningSettings.SchemaFiles)
            {
                string path = Path.Combine(destination, name);
                if (!File.Exists(path)) WriteNewFile(path, Encoding.UTF8.GetBytes(LearningSettings.WithLearning(null, enabled: false)));
            }
        }

        string database = Path.Combine(legacyDirectory, DictionaryDirectory);
        string newDatabase = Path.Combine(destination, DictionaryDirectory);
        string staged = Path.Combine(destination, "migration", SnapshotFile);
        if (File.Exists(staged)) return new(LegacyDictionaryMigration.SnapshotStaged, staged);

        bool hasDatabase = Directory.Exists(database);
        if (hasDatabase && !Directory.Exists(newDatabase) && TryCopyOfflineDatabase(database, newDatabase))
            return new(LegacyDictionaryMigration.DatabaseCopied, null);

        byte[]? snapshot = FindSnapshot(legacyDirectory);
        if (snapshot is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            WriteNewFile(staged, snapshot);
            return new(LegacyDictionaryMigration.SnapshotStaged, staged);
        }
        return hasDatabase ? new(LegacyDictionaryMigration.Unavailable, null) : default;
    }

    static bool TryCopyOfflineDatabase(string source, string destination)
    {
        if (IsReparsePoint(source)) return false;
        string lockPath = Path.Combine(source, "LOCK");
        if (!File.Exists(lockPath) || IsReparsePoint(lockPath)) return false;

        FileStream databaseLock;
        try
        {
            // LevelDB holds a read/write handle on LOCK for as long as the database is open.
            databaseLock = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        using (databaseLock)
        {
            string current = Path.Combine(source, "CURRENT");
            var pointer = new FileInfo(current);
            if (!pointer.Exists || pointer.Length is 0 or > 128 || IsReparsePoint(current)) return false;
            string manifest = File.ReadAllText(current).TrimEnd('\r', '\n');
            if (!ManifestName().IsMatch(manifest) || !File.Exists(Path.Combine(source, manifest))) return false;

            string[] files = Directory.EnumerateFiles(source)
                .Where(path => Path.GetFileName(path) is "CURRENT" || ManifestName().IsMatch(Path.GetFileName(path))
                    || DataFileName().IsMatch(Path.GetFileName(path)))
                .ToArray();
            if (files.Any(IsReparsePoint)) return false;

            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(temporary);
            try
            {
                foreach (string path in files) File.Copy(path, Path.Combine(temporary, Path.GetFileName(path)));
                Directory.Move(temporary, destination);
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    foreach (string path in Directory.EnumerateFiles(temporary)) File.Delete(path);
                    Directory.Delete(temporary);
                }
            }
            return true;
        }
    }

    static byte[]? FindSnapshot(string legacyDirectory)
    {
        string sync = Path.Combine(legacyDirectory, "sync");
        if (!Directory.Exists(sync) || IsReparsePoint(sync)) return null;
        string? installationId = ReadInstallationId(legacyDirectory);
        var candidates = Directory.EnumerateDirectories(sync)
            .Where(path => !IsReparsePoint(path))
            .Select(path => new FileInfo(Path.Combine(path, SnapshotFile)))
            .Where(file => file.Exists && !IsReparsePoint(file.FullName))
            .OrderByDescending(file => string.Equals(file.Directory?.Name, installationId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.LastWriteTimeUtc);
        foreach (FileInfo candidate in candidates)
        {
            try
            {
                using var source = new FileStream(candidate.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (source.Length is 0 or > MaxSnapshotSize) continue;
                byte[] bytes = new byte[(int)source.Length];
                source.ReadExactly(bytes);
                if (IsDictionarySnapshot(bytes)) return bytes;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    static string? ReadInstallationId(string legacyDirectory)
    {
        string path = Path.Combine(legacyDirectory, "installation.yaml");
        if (!File.Exists(path) || IsReparsePoint(path)) return null;
        try
        {
            return File.ReadLines(path)
                .FirstOrDefault(line => line.StartsWith("installation_id:", StringComparison.Ordinal))?
                .Split(':', 2)[1].Trim().Trim('\'', '"');
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static bool IsDictionarySnapshot(byte[] bytes)
    {
        if (bytes[^1] != '\n') return false;
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true));
        try
        {
            int names = 0;
            int types = 0;
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("#@/db_name\t", StringComparison.Ordinal))
                {
                    if (line != "#@/db_name\trime_ice") return false;
                    names++;
                }
                if (line.StartsWith("#@/db_type\t", StringComparison.Ordinal))
                {
                    if (line != "#@/db_type\tuserdb") return false;
                    types++;
                }
            }
            return names == 1 && types == 1;
        }
        catch (DecoderFallbackException) { return false; }
    }

    static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static void WriteNewFile(string path, byte[] data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }
}
