using System.Collections.Specialized;
using System.Drawing.Imaging;
using ShuangpinLink;

internal static class ImageFixture
{
    // Called only by the isolated Windows installation test that owns this desktop.
    internal static void VerifyClipboard(nint window, string folder)
    {
        using var bitmap = new Bitmap(12, 8);
        bitmap.SetPixel(1, 2, Color.Blue);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        byte[] png = stream.ToArray();
        string path = Path.GetFullPath(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png"));
        File.WriteAllBytes(path, png);
        try
        {
            Clipboard.SetFileDropList(new StringCollection { path });
            var source = ClipboardImages.Capture(window, PrivateClipboard.Sequence);
            if (source == null || !source.ReadImage(true).AsSpan().SequenceEqual(png))
                throw new InvalidOperationException("Copying a single PNG file lost its original bytes.");

            Clipboard.SetImage(bitmap);
            source = ClipboardImages.Capture(window, PrivateClipboard.Sequence);
            if (source == null || ImageFormats.Inspect(source.ReadImage(false)) != new ImageFormats.Info("image/png", 12, 8))
                throw new InvalidOperationException("A screenshot did not encode to a correctly sized PNG.");

            uint before = PrivateClipboard.Sequence;
            Clipboard.SetText("Newer synthetic clipboard text");
            var info = ImageFormats.Inspect(png);
            var dib = ClipboardImages.PasteDib(png, info);
            if (ClipboardImages.Write(window, before, path, png, info.Mime, dib))
                throw new InvalidOperationException("A late image overwrote a newer local copy.");
            if (!ClipboardImages.Write(window, PrivateClipboard.Sequence, path, png, info.Mime, dib)
                || !Clipboard.ContainsImage() || Clipboard.GetFileDropList().Cast<string>().Single() != path)
                throw new InvalidOperationException("The received image is missing its bitmap or file-paste representation.");
            if (Clipboard.GetDataObject() is not { } clip || !PrivateClipboard.IsProtected(clip)
                || ClipboardImages.Capture(window, PrivateClipboard.Sequence) != null)
                throw new InvalidOperationException("A received image lost its privacy marker or could echo.");
            Console.WriteLine("Verified image file copy, screenshot encoding, paste formats, no echo and newer-copy priority.");
        }
        finally { Clipboard.Clear(); File.Delete(path); }
    }
}
