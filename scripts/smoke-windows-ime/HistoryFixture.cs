using System.Security.Cryptography;
using ShuangpinLink;

internal static class HistoryFixture
{
    sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async Task Run(string folder)
    {
        string path = Path.Combine(folder, "history-" + Guid.NewGuid().ToString("N") + ".bin");
        var clock = new TestClock();
        var store = new ClipboardHistory(path, clock);
        const string example = "history-test-secret-example-919";
        try
        {
            await store.Clear();
            await store.Add(example);
            if (File.ReadAllBytes(path).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(example)) >= 0)
                throw new InvalidDataException("Plaintext found on disk.");
            var restored = new ClipboardHistory(path, clock);
            if ((await restored.List()).Single().Text != example) throw new InvalidDataException("Restart lost history.");
            await Task.WhenAll(Enumerable.Range(0, 53).Select(i => store.Add($"copy-{i}")));
            await store.Add("copy-50");
            var entries = await store.List();
            if (entries.Count != 50 || entries[0].Text != "copy-50" || entries[1].Text != "copy-52"
                || entries[^1].Text != "copy-3") throw new InvalidDataException("Capacity, ordering or duplicate error.");
            await store.Delete(entries[0].Id);
            await store.Add(new string('x', 65537));
            if ((await store.List()).Count != 49) throw new InvalidDataException("Delete or size limit failed.");
            clock.Now += TimeSpan.FromDays(7);
            if ((await store.List()).Count != 0 || File.Exists(path)) throw new InvalidDataException("Expiry failed.");
            await store.Add(example);
            byte[] corrupted = File.ReadAllBytes(path);
            corrupted[^1] ^= 1;
            File.WriteAllBytes(path, corrupted);
            try { await store.List(); throw new InvalidDataException("Modified archive accepted."); }
            catch (CryptographicException) { }
            await store.Clear();
            if ((await store.List()).Count != 0) throw new InvalidDataException("Clear failed.");
            Console.WriteLine("Clipboard history encryption, persistence, FIFO writes, deduplication, expiry, deletion, clear and tamper rejection passed.");
        }
        finally { await store.Clear(); }
    }
}
