using System.Security.Cryptography;
using System.Text.Json;

namespace ShuangpinLink;

internal sealed record HistoryEntry(Guid Id, long CopiedAt, string Text);

internal sealed class ClipboardHistory(string path, TimeProvider? clock = null)
{
    internal const int Capacity = 50;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    static readonly byte[] Purpose = "ShuangpinLink.ClipboardHistory.1"u8.ToArray();
    const int MaximumArchiveBytes = Capacity * (ClipboardWire.TextLimit * 6 + 256) + 65536;
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object gate = new();
    Task pending = Task.CompletedTask;

    // Serializing operations on the worker pool preserves copy/delete order while DPAPI runs off the message loop.
    Task<T> Enqueue<T>(Func<T> work)
    {
        lock (gate)
        {
            var next = pending.ContinueWith(completed =>
            {
                _ = completed.Exception;
                return work();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            pending = next.ContinueWith(done => { _ = done.Exception; }, TaskScheduler.Default);
            return next;
        }
    }

    internal Task Drain() { lock (gate) return pending; }

    internal Task<List<HistoryEntry>> List() => Enqueue(() => Edit(null));
    internal Task Prune() => Enqueue(() => { Edit(null); return true; });
    internal Task Add(string text) => Enqueue(() => Edit(entries =>
    {
        if (text.Length == 0 || text.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(text) > ClipboardWire.TextLimit)
            return false;
        entries.RemoveAll(entry => entry.Text == text);
        entries.Insert(0, new HistoryEntry(Guid.NewGuid(), time.GetUtcNow().ToUnixTimeMilliseconds(), text));
        if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
        return true;
    }));
    internal Task Delete(Guid id) => Enqueue(() => Edit(entries => entries.RemoveAll(entry => entry.Id == id) != 0));
    internal Task Clear() => Enqueue(() =>
    {
        if (!Directory.Exists(Path.GetDirectoryName(path))) return true;
        File.Delete(path + ".new");
        File.Delete(path);
        return true;
    });

    internal static List<HistoryEntry> ReadSnapshot(string path)
    {
        var entries = new List<HistoryEntry>();
        var file = new FileInfo(path);
        if (file.Exists)
        {
            if (file.Length > MaximumArchiveBytes) throw new InvalidDataException("剪贴板历史文件过大。");
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Purpose, DataProtectionScope.CurrentUser);
            try { entries = JsonSerializer.Deserialize<List<HistoryEntry>>(plain) ?? throw new InvalidDataException(); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return entries;
    }

    List<HistoryEntry> Edit(Func<List<HistoryEntry>, bool>? change)
    {
        var entries = ReadSnapshot(path);
        long cutoff = (time.GetUtcNow() - Retention).ToUnixTimeMilliseconds();
        bool expired = entries.RemoveAll(entry => entry.CopiedAt <= cutoff) != 0;
        if ((change?.Invoke(entries) ?? false) || expired) Save(entries);
        else if (Directory.Exists(Path.GetDirectoryName(path))) File.Delete(path + ".new");
        return entries;
    }

    void Save(List<HistoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (entries.Count == 0)
        {
            File.Delete(path + ".new");
            File.Delete(path);
            return;
        }
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(entries);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plain, Purpose, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path + ".new", encrypted);
            File.Move(path + ".new", path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            File.Delete(path + ".new");
        }
    }
}
