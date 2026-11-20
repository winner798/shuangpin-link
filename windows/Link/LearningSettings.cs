using System.Text.RegularExpressions;

namespace ShuangpinLink;

internal static class LearningSettings
{
    internal static IReadOnlyList<string> SchemaFiles { get; } = ["double_pinyin_mspy.custom.yaml", "rime_ice.custom.yaml"];

    static readonly Regex PatchKey = new("^(?:patch|'patch'|\"patch\")[ \\t]*:", RegexOptions.CultureInvariant);
    static readonly Regex PatchBlock = new("^(?:patch|'patch'|\"patch\")[ \\t]*:[ \\t]*(?<empty>\\{[ \\t]*\\})?[ \\t]*(?:#.*)?$", RegexOptions.CultureInvariant);
    static readonly Regex LearningEntry = new("^(?<indent> +)(?<key>(?:translator/enable_user_dict|'translator/enable_user_dict'|\"translator/enable_user_dict\")[ \\t]*:[ \\t]*)(?<value>[^#]*?)(?<rest>[ \\t]*(?:#.*)?)$", RegexOptions.CultureInvariant);

    internal static bool Enabled(string userDirectory) => SchemaFiles.All(name =>
    {
        string path = Path.Combine(userDirectory, name);
        if (!File.Exists(path)) return true;
        var lines = ReadLines(File.ReadAllText(path));
        int header = lines.FindIndex(line => PatchKey.IsMatch(line));
        if (header < 0 || !PatchBlock.IsMatch(lines[header])) return true;
        var (end, indent) = Section(lines, header);
        return !lines.Skip(header + 1).Take(end - header - 1).Select(line => LearningEntry.Match(line)).Any(match =>
            match.Success && match.Groups["indent"].Length == indent &&
            match.Groups["value"].Value.Trim().Trim('\'', '"').Equals("false", StringComparison.OrdinalIgnoreCase));
    });

    internal static string WithLearning(string? previous, bool enabled)
    {
        string value = enabled ? "true" : "false";
        if (string.IsNullOrWhiteSpace(previous)) return $"patch:\n  translator/enable_user_dict: {value}\n";
        string newline = previous.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = ReadLines(previous);
        int header = lines.FindIndex(line => PatchKey.IsMatch(line));
        if (header < 0)
        {
            int end = lines.FindIndex(line => line == "...");
            if (end < 0) end = lines.Count;
            lines.Insert(end, "patch:");
            lines.Insert(end + 1, $"  translator/enable_user_dict: {value}");
            return string.Join(newline, lines) + newline;
        }

        var block = PatchBlock.Match(lines[header]);
        if (!block.Success) throw new InvalidDataException("请将输入方案自定义补丁改成常规 YAML 映射后再修改词库学习。");
        if (block.Groups["empty"].Success)
        {
            var empty = block.Groups["empty"];
            lines[header] = lines[header].Remove(empty.Index, empty.Length);
        }

        var (sectionEnd, indent) = Section(lines, header);
        var entryLines = Enumerable.Range(header + 1, sectionEnd - header - 1).Where(index =>
        {
            var match = LearningEntry.Match(lines[index]);
            return match.Success && match.Groups["indent"].Length == indent;
        }).ToArray();
        if (entryLines.Length > 1) throw new InvalidDataException("输入方案自定义补丁含有重复的词库学习设置。");
        if (entryLines.Length == 1)
        {
            var entry = LearningEntry.Match(lines[entryLines[0]]);
            lines[entryLines[0]] = entry.Groups["indent"].Value + entry.Groups["key"].Value + value + entry.Groups["rest"].Value;
        }
        else
        {
            if (lines.Skip(header + 1).Take(sectionEnd - header - 1).Any(line =>
                line.Length > indent && Indentation(line) == indent && line[indent] == '-'))
                throw new InvalidDataException("请将输入方案自定义补丁改成常规 YAML 映射后再修改词库学习。");
            lines.Insert(header + 1, new string(' ', indent) + $"translator/enable_user_dict: {value}");
        }
        return string.Join(newline, lines);
    }

    static List<string> ReadLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

    static (int End, int Indent) Section(List<string> lines, int header)
    {
        int end = header + 1;
        int indent = int.MaxValue;
        for (; end < lines.Count; end++)
        {
            var line = lines[end];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            int level = Indentation(line);
            if (level == 0) break;
            indent = Math.Min(indent, level);
        }
        return (end, indent == int.MaxValue ? 2 : indent);
    }

    static int Indentation(string line) => line.Length - line.TrimStart(' ').Length;
}
