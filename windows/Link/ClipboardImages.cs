using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ShuangpinLink;

internal sealed record ClipboardImageSource(byte[]? Bytes, string? Path, bool IsDib = false)
{
    internal byte[] ReadImage(bool keepMetadata)
    {
        byte[] bytes;
        if (Path != null)
        {
            var root = System.IO.Path.GetPathRoot(Path);
            if (root == null || !System.IO.Path.IsPathFullyQualified(Path) || new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
                throw new InvalidDataException("仅支持本机图片文件。");
            var info = new FileInfo(Path);
            if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Offline)) != 0)
                throw new InvalidDataException("请先复制本机图片。");
            for (var dir = info.Directory; dir != null; dir = dir.Parent)
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("请直接复制原始图片文件。");
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > ImageWire.MaxSize) throw new InvalidDataException("图片超过 8 MiB 或为空。");
            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
        }
        else bytes = IsDib ? ClipboardImages.PngFromDib(Bytes!) : Bytes!;
        ImageFormats.Inspect(bytes);
        if (keepMetadata) return bytes;
        try { return ImageFormats.WithoutMetadata(bytes); }
        finally { if (!ReferenceEquals(bytes, Bytes)) CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal static class ClipboardImages
{
    const uint Dib = 8, FileDrop = 15, DibV5 = 17;
    const int MaxDibBytes = 128_000_000 + 4096;
    static readonly string[] ImageFormatsOnClipboard = ["PNG", "image/png", "image/jpeg", "JFIF", "image/gif", "GIF", "image/webp", "WebP"];

    internal static ClipboardImageSource? Capture(nint window, uint sequence, bool probeOnly = false)
    {
        if (!OpenClipboard(window)) throw new ExternalException();
        try
        {
            if (sequence != PrivateClipboard.Sequence || IsClipboardFormatAvailable(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"))
                || IsClipboardFormatAvailable(RegisterClipboardFormatW("CanIncludeInClipboardHistory"))
                || IsClipboardFormatAvailable(RegisterClipboardFormatW("CanUploadToCloudClipboard"))) return null;
            if (IsClipboardFormatAvailable(FileDrop))
            {
                nint drop = GetClipboardData(FileDrop);
                if (drop == 0 || DragQueryFileW(drop, uint.MaxValue, null, 0) != 1) return null;
                uint length = DragQueryFileW(drop, 0, null, 0);
                if (length is 0 or > 32767) return null;
                var name = new StringBuilder((int)length + 1);
                if (DragQueryFileW(drop, 0, name, length + 1) != length) return null;
                string path = name.ToString();
                if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
                    || path.AsSpan(2).Contains(':') || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".gif" or ".webp" or ".jpg" or ".jpeg")) return null;
                return new(null, path);
            }
            foreach (string name in ImageFormatsOnClipboard)
            {
                uint format = RegisterClipboardFormatW(name);
                if (!IsClipboardFormatAvailable(format)) continue;
                if (probeOnly) return new(null, null);
                byte[] encoded = ReadGlobal(format, ImageWire.MaxSize + 64);
                // GlobalSize may include trailing alignment bytes. RIFF records its own length.
                int size = encoded.Length;
                if (size >= 12 && encoded.AsSpan(0, 4).SequenceEqual("RIFF"u8))
                {
                    long riffSize = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4)) + 8L;
                    if (riffSize <= size && size - riffSize <= 64) size = (int)riffSize;
                }
                else while (size > 0 && encoded[size - 1] == 0 && encoded.Length - size < 64) size--;
                if (size != encoded.Length) Array.Resize(ref encoded, size);
                return new(encoded, null);
            }
            uint dib = IsClipboardFormatAvailable(DibV5) ? DibV5 : Dib;
            if (!IsClipboardFormatAvailable(dib)) return null;
            return new(probeOnly ? null : ReadGlobal(dib, MaxDibBytes), null, true);
        }
        finally { CloseClipboard(); }
    }

    static byte[] ReadGlobal(uint format, int limit)
    {
        nint handle = GetClipboardData(format);
        nuint size = GlobalSize(handle);
        if (size == 0 || size > (nuint)limit) throw new InvalidDataException("剪贴板图片过大或无法读取。");
        nint data = GlobalLock(handle);
        if (data == 0) throw new ExternalException();
        try { var bytes = new byte[(int)size]; Marshal.Copy(data, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }

    internal static byte[] PngFromDib(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidDataException("位图头无效。");
        int header = BitConverter.ToInt32(dib), width = BitConverter.ToInt32(dib, 4), signedHeight = BitConverter.ToInt32(dib, 8);
        if (header is not (40 or 52 or 56 or 108 or 124) || header > dib.Length || width is < 1 or > 16384
            || signedHeight is 0 or < -16384 or > 16384 || width * (long)Math.Abs(signedHeight) > 32_000_000)
            throw new InvalidDataException("位图尺寸不受支持。");
        int bits = BitConverter.ToUInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16);
        if (BitConverter.ToUInt16(dib, 12) != 1 || bits is not (24 or 32) || compression is not (0 or 3))
            throw new InvalidDataException("请将位图复制为 PNG 图片。");
        uint colors = BitConverter.ToUInt32(dib, 32);
        if (colors > 256) throw new InvalidDataException("位图颜色表过大。");
        int offset = header + (compression == 3 && header == 40 ? 12 : 0) + 4 * (int)colors;
        if (header == 124 && BitConverter.ToUInt32(dib, 116) != 0 && BitConverter.ToUInt32(dib, 112) == offset)
        {
            uint profileSize = BitConverter.ToUInt32(dib, 116);
            if (profileSize > dib.Length - offset) throw new InvalidDataException("位图颜色配置无效。");
            offset += (int)profileSize;
        }
        int height = Math.Abs(signedHeight), stride = ((width * bits + 31) / 32) * 4;
        if (offset + (long)stride * height > dib.Length) throw new InvalidDataException("位图像素不完整。");
        if (compression == 3 && (BitConverter.ToInt32(dib, 40) != 0xff0000 || BitConverter.ToInt32(dib, 44) != 0xff00
            || BitConverter.ToInt32(dib, 48) != 0xff)) throw new InvalidDataException("位图通道排列不受支持。");
        bool hasAlpha = bits == 32 && compression == 3 && header >= 56 && BitConverter.ToUInt32(dib, 52) == 0xff000000;
        var pixelFormat = bits == 24 ? PixelFormat.Format24bppRgb : hasAlpha ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppRgb;
        using var bitmap = new Bitmap(width, height, pixelFormat);
        var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, pixelFormat);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(dib, offset + (signedHeight > 0 ? height - 1 - y : y) * stride, locked.Scan0 + y * locked.Stride, width * (bits / 8));
        }
        finally { bitmap.UnlockBits(locked); }
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        if (png.Length > ImageWire.MaxSize) throw new InvalidDataException("截图编码后超过 8 MiB。");
        return png.ToArray();
    }

    internal static byte[]? PasteDib(byte[] original, ImageFormats.Info info)
    {
        // Original compressed bytes and a file representation always accompany this optional preview.
        if ((long)info.Width * info.Height > 12_000_000) return null;
        try
        {
            using var source = new MemoryStream(original, writable: false);
            using var frame = Image.FromStream(source, false, false);
            using var rgb = new Bitmap(info.Width, info.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(rgb))
            {
                graphics.Clear(Color.White);
                graphics.DrawImage(frame, new Rectangle(0, 0, info.Width, info.Height));
            }
            using var bmp = new MemoryStream();
            rgb.Save(bmp, ImageFormat.Bmp);
            return bmp.GetBuffer().AsSpan(14, checked((int)bmp.Length - 14)).ToArray();
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException) { return null; }
    }

    internal static bool Write(nint window, uint sequence, string path, byte[] original, string mime, byte[]? dib)
    {
        string native = mime switch { "image/png" => "PNG", "image/jpeg" => "JFIF", "image/gif" => "GIF", "image/webp" => "WebP", _ => throw new InvalidDataException() };
        var drop = new byte[20 + Encoding.Unicode.GetByteCount(path) + 4];
        drop[0] = 20; drop[16] = 1; // DROPFILES.pFiles, fWide
        Encoding.Unicode.GetBytes(path.AsSpan(), drop.AsSpan(20));
        var formats = new List<(uint, byte[])> { (RegisterClipboardFormatW(native), original), (RegisterClipboardFormatW(mime), original), (FileDrop, drop) };
        if (dib != null) formats.Add((Dib, dib));
        return PrivateClipboard.WriteFormats(window, sequence, formats);
    }

    [DllImport("user32.dll")] static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern uint DragQueryFileW(nint drop, uint index, StringBuilder? name, uint size);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint handle);
}
