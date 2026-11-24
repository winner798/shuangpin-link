using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using System.Text;
using ShuangpinLink;

internal static class ImageNetworkFixture
{
    sealed record Pair(string Server, Device Owner, Device Peer, string Key);
    sealed record Capability(bool PeerEnabled);
    sealed record Manifest(long Revision, string Id, string Sender, long SentAt, int Size);
    sealed record Chunk(string Envelope);

    static Pair Read(string path) => JsonSerializer.Deserialize<Pair>(File.ReadAllBytes(path), Relay.Json)!;

    internal static int Seed(string path)
    {
        var pair = Read(path);
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShuangpinLink", "state", "device.bin");
        var session = new Session(pair.Server, pair.Owner, pair.Key, ShareDictionary: false, ImagesEnabled: true);
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        using var file = new FileStream(state, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session, Relay.Json), null, DataProtectionScope.CurrentUser));
        return 0;
    }

    internal static int Run(string path)
    {
        ApplicationConfiguration.Initialize();
        int exit = 1;
        using var form = new Form { Text = "Synthetic paired image transfer", ShowInTaskbar = false };
        form.Shown += async (_, _) =>
        {
            try { await Verify(Read(path), Path.GetDirectoryName(path)!); exit = 0; }
            catch (Exception error) { CaptureControlStatus(); Console.Error.WriteLine(error); }
            finally { Clipboard.Clear(); form.Close(); }
        };
        Application.Run(form);
        return exit;
    }

    static async Task Verify(Pair pair, string directory)
    {
        var peer = new Relay(pair.Server, pair.Peer.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        var token = timeout.Token;
        while ((await peer.Send<Capability>(HttpMethod.Put, "image/capability?enabled=1", token, protocol: "v3"))?.PeerEnabled != true)
            await Task.Delay(200, token);
        await Task.Delay(5800, token); // The installed client's initial peer-capability poll is every five seconds.
        using var screenshot = new Bitmap(520, 310, PixelFormat.Format24bppRgb);
        var pixels = screenshot.LockBits(new Rectangle(0, 0, screenshot.Width, screenshot.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] noise = new byte[pixels.Stride * pixels.Height];
            new Random(814).NextBytes(noise);
            Marshal.Copy(noise, 0, pixels.Scan0, noise.Length);
        }
        finally { screenshot.UnlockBits(pixels); }
        string original = Path.Combine(directory, "fixture.png");
        screenshot.Save(original, ImageFormat.Png);
        byte[] expected = ImageFormats.WithoutMetadata(await File.ReadAllBytesAsync(original, token));
        Clipboard.SetFileDropList(new StringCollection { original });
        await Task.Delay(550, token);
        var probe = ClipboardImages.Capture(0, PrivateClipboard.Sequence);
        Console.WriteLine($"Synthetic file is readable: {probe?.ReadImage(false).Length == expected.Length}; private marker: {Clipboard.GetDataObject() is { } d && PrivateClipboard.IsProtected(d)}");
        Manifest? sent = null;
        while (sent == null)
            sent = await peer.Send<Manifest>(HttpMethod.Get, "image?after=0&wait=2", token, protocol: "v3");
        if (sent.Sender != ClipboardWire.Owner || sent.Size != expected.Length || ImageWire.Count(sent.Size) < 2)
            throw new InvalidDataException("The installed client did not publish the original multi-block PNG.");
        byte[] received = new byte[sent.Size];
        for (int i = 0; i < ImageWire.Count(sent.Size); i++)
        {
            var chunk = await peer.Send<Chunk>(HttpMethod.Get, $"image/{sent.Id}/{i}", token, protocol: "v3");
            ImageWire.Open(pair.Key, sent.Sender, sent.Id, sent.SentAt, sent.Size, i, chunk!.Envelope, received);
        }
        if (!received.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("The peer could not recover the copied PNG.");
        using var jpeg = new MemoryStream();
        screenshot.Save(jpeg, ImageFormat.Jpeg);
        byte[] reply = jpeg.ToArray();
        string id = Guid.NewGuid().ToString("N");
        long sentAt = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sent.SentAt + 1);
        await peer.Send<object>(HttpMethod.Put, $"image/{id}?sentAt={sentAt}&size={reply.Length}", token, protocol: "v3");
        for (int i = 0; i < ImageWire.Count(reply.Length); i++)
            await peer.Send<object>(HttpMethod.Put, $"image/{id}/{i}", token, new Chunk(ImageWire.Seal(pair.Key, ClipboardWire.Peer, id, sentAt, reply.Length, i, reply)), "v3");
        while (true)
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count == 1 && files[0] is string name && Path.GetExtension(name) == ".jpg")
            {
                if (!(await File.ReadAllBytesAsync(name, token)).SequenceEqual(reply) || !Clipboard.ContainsImage()
                    || Clipboard.GetDataObject() is not { } data || !PrivateClipboard.IsProtected(data))
                    throw new InvalidDataException("Peer JPEG was not pasteable with exact bytes, bitmap format and privacy marker.");
                break;
            }
            await Task.Delay(100, token);
        }
        Console.WriteLine("Installed Windows tray uploaded a copied multi-block PNG and received a peer JPEG through the authenticated TLS relay.");
    }

    private delegate bool EnumCallback(nint handle, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback cb, nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint handle, EnumCallback cb, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wp, StringBuilder lp, uint flags, uint ms, out nint result);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wp, nint lp, uint flags, uint ms, out nint result);

    static void CaptureControlStatus()
    {
        // Only the installed test application's non-editable status controls are printed.
        var ids = Process.GetProcessesByName("ShuangpinLink").Select(p => (uint)p.Id).ToHashSet();
        EnumWindows((parent, _) =>
        {
            GetWindowThreadProcessId(parent, out uint pid);
            if (!ids.Contains(pid)) return true;
            void Visit(bool selectTab) => EnumChildWindows(parent, (child, param) =>
            {
                var text = new StringBuilder(160);
                SendMessageTimeoutW(child, 0x000d, text.Capacity, text, 2, 500, out nint result);
                string value = text.ToString();
                if (selectTab && value == "设备互通") SendMessageTimeoutW(child, 0x00f5, 0, 0, 2, 500, out result);
                if (!selectTab && (value.Contains("图片") || value.Contains("配对") || value.Contains("剪贴板") || value.Contains("图片通道")))
                    Console.WriteLine("Installed status: " + value);
                return true;
            }, 0);
            Visit(true);
            Visit(false);
            return true;
        }, 0);
    }
}
