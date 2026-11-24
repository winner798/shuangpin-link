using System.Buffers.Binary;
using System.Text;

namespace ShuangpinLink;

// Read container headers without decoding pixels. Metadata removal copies the compressed
// picture/animation chunks, so transparency, frame delays, and the original codec survive.
internal static class ImageFormats
{
    internal sealed record Info(string Mime, int Width, int Height);
    internal const int MaxBytes = 8 * 1024 * 1024;
    internal static Info Inspect(byte[] bytes) => new Reader(bytes, null).Parse();
    internal static byte[] WithoutMetadata(byte[] bytes)
    {
        using var output = new MemoryStream();
        new Reader(bytes, output).Parse();
        return output.ToArray();
    }

    private sealed class Reader(byte[] data, MemoryStream? output)
    {
        private int width, height, frames;
        private long framePixels;
        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
        {
            uint crc = (uint)i;
            for (int j = 0; j < 8; j++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            return crc;
        }).ToArray();
        private static void Require(bool valid)
        {
            if (!valid) throw new InvalidDataException("图片格式损坏、尺寸过大或包含不支持的结构。");
        }
        private void Bounds(int at, int size) => Require(at >= 0 && size >= 0 && at <= data.Length - size);
        private int U16(int p, bool little = false) => little ? data[p] | data[p + 1] << 8 : data[p] << 8 | data[p + 1];
        private int U24(int p) => U16(p, true) | data[p + 2] << 16;
        private int U32(int p, bool little = false)
        {
            uint n = little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
            Require(n <= int.MaxValue);
            return (int)n;
        }
        private bool Is(int p, string text) => p >= 0 && p <= data.Length - text.Length &&
            data.AsSpan(p, text.Length).SequenceEqual(Encoding.ASCII.GetBytes(text));
        private void Copy(int p, int n) => output?.Write(data, p, n);
        // Rebuild the one display tag we need. Never follow GPS, thumbnail or vendor pointers.
        private byte[]? Orientation(int start, int size)
        {
            if (size >= 6 && Is(start, "Exif\0\0")) { start += 6; size -= 6; }
            bool little = Is(start, "II");
            if (size < 8 || (!little && !Is(start, "MM")) || U16(start + 2, little) != 42) return null;
            uint offset = little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + 4))
                : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + 4));
            if (offset < 8 || offset > size - 2) return null;
            int directory = start + (int)offset;
            int entries = U16(directory, little);
            if (entries > (size - (int)offset - 2) / 12) return null;
            for (int i = 0; i < entries; i++)
            {
                int field = directory + 2 + 12 * i;
                if (U16(field, little) != 274 || U16(field + 2, little) != 3) continue;
                if (!data.AsSpan(field + 4, 4).SequenceEqual(little ? new byte[] { 1, 0, 0, 0 } : new byte[] { 0, 0, 0, 1 })) continue;
                int value = U16(field + 8, little);
                if (value is < 2 or > 8) return null;
                byte[] tiff = Convert.FromHexString("4D4D002A00000008000101120003000000010000000000000000");
                tiff[19] = (byte)value;
                return tiff;
            }
            return null;
        }
        private static uint Crc(ReadOnlySpan<byte> bytes)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in bytes) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
            return ~crc;
        }
        private void Canvas(int w, int h)
        {
            Require(w is > 0 and <= 16384 && h is > 0 and <= 16384 && (long)w * h <= 32_000_000);
            if (width != 0) Require(w == width && h == height);
            width = w; height = h;
        }
        private void Frame(int x, int y, int w, int h)
        {
            Require(w > 0 && h > 0 && x >= 0 && y >= 0 && x <= width - w && y <= height - h);
            framePixels += (long)w * h;
            Require(++frames <= 512 && framePixels <= 256_000_000);
        }
        internal Info Parse()
        {
            Require(data.Length is >= 12 and <= MaxBytes);
            string mime;
            if (data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) { Png(); mime = "image/png"; }
            else if (data[0] == 255 && data[1] == 216) { Jpeg(); mime = "image/jpeg"; }
            else if (Is(0, "GIF87a") || Is(0, "GIF89a")) { Gif(); mime = "image/gif"; }
            else if (Is(0, "RIFF") && Is(8, "WEBP")) { Webp(); mime = "image/webp"; }
            else throw new InvalidDataException("图片互传支持 PNG、JPEG、WebP 和 GIF。");
            return new Info(mime, width, height);
        }
        private void Png()
        {
            Copy(0, 8);
            bool pixels = false;
            int declared = 0;
            for (int p = 8, chunks = 0; ;)
            {
                Bounds(p, 12); Require(++chunks <= 65536);
                int n = U32(p), b = p + 8;
                Bounds(b, n); Bounds(b + n, 4);
                string type = Encoding.ASCII.GetString(data, p + 4, 4);
                Require(Crc(data.AsSpan(p + 4, n + 4)) == BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(b + n)));
                if (type == "IHDR") { Require(p == 8 && n == 13); Canvas(U32(b), U32(b + 4)); }
                Require(width > 0);
                if (type == "IDAT" && n > 0) pixels = true;
                if (type == "acTL") { Require(n == 8 && declared == 0); declared = U32(b); Require(declared is > 0 and <= 512); }
                if (type == "fcTL") { Require(n == 26 && declared > 0); Frame(U32(b + 12), U32(b + 16), U32(b + 4), U32(b + 8)); }
                if (type is "IHDR" or "PLTE" or "IDAT" or "IEND" or "tRNS" or "sRGB" or "gAMA" or "cHRM" or "acTL" or "fcTL" or "fdAT")
                    Copy(p, 12 + n);
                else if (type == "eXIf" && output != null && Orientation(b, n) is { } orientation)
                {
                    byte[] chunk = new byte[12 + orientation.Length];
                    BinaryPrimitives.WriteInt32BigEndian(chunk, orientation.Length);
                    "eXIf"u8.CopyTo(chunk.AsSpan(4));
                    orientation.CopyTo(chunk, 8);
                    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), Crc(chunk.AsSpan(4, chunk.Length - 8)));
                    output.Write(chunk);
                }
                else Require((data[p + 4] & 32) != 0); // Unknown critical chunks cannot be rendered safely.
                p = b + n + 4;
                if (type == "IEND") { Require(n == 0 && pixels && p == data.Length && frames == declared); return; }
            }
        }
        private void Jpeg()
        {
            Copy(0, 2);
            int p = 2;
            bool scan = false;
            while (true)
            {
                int marker = p;
                Bounds(p, 2); Require(data[p++] == 255);
                while (p < data.Length && data[p] == 255) p++;
                Bounds(p, 1);
                int code = data[p++];
                if (code == 217) { Require(scan && width > 0 && p == data.Length); Copy(marker, p - marker); return; }
                Require(code != 0 && code != 216 && code is not (>= 208 and <= 215));
                if (code == 1) { Copy(marker, p - marker); continue; }
                Bounds(p, 2);
                int n = U16(p);
                Require(n >= 2); Bounds(p, n);
                if (code is >= 192 and <= 207 && code is not (196 or 200 or 204))
                {
                    Require(n >= 8); Canvas(U16(p + 5), U16(p + 3));
                }
                // Strip APP/COM, including EXIF, XMP, IPTC, JFXX thumbnails and vendor data.
                if (((code < 224 || code > 239) && code != 254) || (code == 238 && n == 14 && Is(p + 2, "Adobe")))
                    Copy(marker, p + n - marker);
                else if (code == 225 && output != null && n >= 8 && Is(p + 2, "Exif\0\0") && Orientation(p + 2, n - 2) is { } orientation)
                {
                    output.Write(new byte[] { 255, 225, 0, (byte)(orientation.Length + 8) });
                    output.Write("Exif\0\0"u8);
                    output.Write(orientation);
                }
                p += n;
                if (code != 218) continue;
                Require(width > 0); scan = true;
                int start = p;
                while (true)
                {
                    int next = Array.IndexOf(data, (byte)255, p);
                    Require(next >= 0); p = next + 1;
                    while (p < data.Length && data[p] == 255) p++;
                    Bounds(p, 1);
                    if (data[p] == 0 || data[p] is >= 208 and <= 215) { p++; continue; }
                    Copy(start, next - start); p = next; break;
                }
            }
        }
        private int GifBlocks(int p)
        {
            while (true) { Bounds(p, 1); int n = data[p++]; if (n == 0) return p; Bounds(p, n); p += n; }
        }
        private void Gif()
        {
            Bounds(0, 13); Canvas(U16(6, true), U16(8, true));
            int p = 13;
            if ((data[10] & 128) != 0) p += 3 * (2 << (data[10] & 7));
            Bounds(0, p); Copy(0, p);
            while (true)
            {
                int start = p; Bounds(p, 1);
                int kind = data[p++];
                if (kind == 59) { Require(frames > 0 && p == data.Length); Copy(start, 1); return; }
                if (kind == 44)
                {
                    Bounds(p, 9); Frame(U16(p, true), U16(p + 2, true), U16(p + 4, true), U16(p + 6, true));
                    int flags = data[p + 8]; p += 9;
                    if ((flags & 128) != 0) p += 3 * (2 << (flags & 7));
                    Bounds(p, 2); Require(data[p] is >= 2 and <= 8 && data[p + 1] > 0);
                    p = GifBlocks(p + 1); Copy(start, p - start);
                }
                else
                {
                    Require(kind == 33); Bounds(p, 2);
                    int ext = data[p++];
                    int end = GifBlocks(p);
                    bool control = ext == 249 && data[p] == 4 && end - p == 6;
                    bool loop = ext == 255 && end - p == 17 && data[p] == 11 && (Is(p + 1, "NETSCAPE2.0") || Is(p + 1, "ANIMEXTS1.0"))
                        && data[p + 12] == 3 && data[p + 13] == 1;
                    if (control || loop) Copy(start, end - start);
                    p = end;
                }
            }
        }
        private (int W, int H) WebpDimensions(string type, int p, int n)
        {
            if (type == "VP8L") { Require(n >= 5 && data[p] == 47); return (1 + ((data[p + 1] | data[p + 2] << 8) & 16383), 1 + ((U24(p + 2) >> 6) & 16383)); }
            Require(n >= 10 && data[p + 3] == 157 && data[p + 4] == 1 && data[p + 5] == 42);
            return (U16(p + 6, true) & 16383, U16(p + 8, true) & 16383);
        }
        private void Webp()
        {
            Require(U32(4, true) == data.Length - 8); Copy(0, 12);
            bool pixels = false;
            for (int p = 12, chunks = 0; p < data.Length;)
            {
                Bounds(p, 8); Require(++chunks <= 65536);
                string type = Encoding.ASCII.GetString(data, p, 4); int n = U32(p + 4, true), b = p + 8;
                Bounds(b, n); int end = b + n + (n & 1); Bounds(p, end - p);
                if (type == "VP8X")
                {
                    Require(p == 12 && n == 10); Canvas(1 + U24(b + 4), 1 + U24(b + 7));
                    Copy(p, 8); output?.WriteByte((byte)(data[b] & ~44)); Copy(b + 1, end - b - 1);
                }
                else if (type is "VP8 " or "VP8L")
                {
                    var (w, h) = WebpDimensions(type, b, n); Canvas(w, h); Require(!pixels && frames == 0);
                    pixels = true; Copy(p, end - p);
                }
                else if (type == "ANMF")
                {
                    Require(n >= 24 && !pixels);
                    int w = 1 + U24(b + 6), h = 1 + U24(b + 9); Frame(2 * U24(b), 2 * U24(b + 3), w, h);
                    bool payload = false;
                    for (int s = b + 16; s < b + n;)
                    {
                        Require(b + n - s >= 8); int len = U32(s + 4, true);
                        Require(len <= b + n - s - 8 && (len & 1) <= b + n - s - 8 - len);
                        string t = Encoding.ASCII.GetString(data, s, 4);
                        if (t is "VP8 " or "VP8L") { Require(!payload && WebpDimensions(t, s + 8, len) == (w, h)); payload = true; }
                        else Require(t == "ALPH" && !payload);
                        s += 8 + len + (len & 1);
                    }
                    Require(payload); Copy(p, end - p);
                }
                else if (type is "ALPH" or "ANIM") Copy(p, end - p);
                else if (type == "EXIF" && output != null && Is(12, "VP8X") && Orientation(b, n) is { } orientation)
                {
                    output.GetBuffer()[20] |= 8;
                    output.Write("EXIF"u8);
                    output.Write(new byte[] { (byte)orientation.Length, 0, 0, 0 });
                    output.Write(orientation);
                }
                p = end;
            }
            Require(width > 0 && (pixels || frames > 0));
            if (output != null) { output.Position = 4; Span<byte> n = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(n, (int)output.Length - 8); output.Write(n); }
        }
    }
}
