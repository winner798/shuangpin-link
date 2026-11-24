using System.Security.Cryptography;
using System.Text;

namespace ShuangpinLink;

internal static class ImageWire
{
    const string Domain = "ime-sync/v3/image";
    internal const int ChunkSize = 262144;
    internal const int MaxSize = 8 * 1024 * 1024;
    internal static int Count(int size) => size is > 0 and <= MaxSize ? (size + ChunkSize - 1) / ChunkSize
        : throw new InvalidDataException("图片大小无效或超过 8 MiB。");

    static int Length(string id, long sentAt, int size, int index)
    {
        if (!ClipboardWire.ValidId(id) || sentAt <= 0 || index < 0 || index >= Count(size))
            throw new InvalidDataException("图片分块无效。");
        return Math.Min(ChunkSize, size - index * ChunkSize);
    }

    static byte[] Header(string sender, string id, long sentAt, int size, int index) =>
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"{Domain}\n{sender}\n{id}\n{sentAt}\n{size}\n{index}\n{Count(size)}"));

    internal static string Seal(string key, string sender, string id, long sentAt, int totalSize, int index, byte[] source)
    {
        int length = Length(id, sentAt, totalSize, index);
        if (source.Length != totalSize) throw new InvalidDataException("图片长度不匹配。");
        var packet = new byte[length + 28];
        RandomNumberGenerator.Fill(packet.AsSpan(0, 12));
        using var cipher = ClipboardWire.DirectionalCipher(key, sender, Domain);
        cipher.Encrypt(packet.AsSpan(0, 12), source.AsSpan(index * ChunkSize, length),
            packet.AsSpan(12, length), packet.AsSpan(12 + length), Header(sender, id, sentAt, totalSize, index));
        return Convert.ToBase64String(packet);
    }

    internal static void Open(string key, string sender, string id, long sentAt, int totalSize, int index, string envelope, byte[] destination)
    {
        int length = Length(id, sentAt, totalSize, index);
        if (destination.Length != totalSize || envelope.Length != 4 * ((length + 30) / 3))
            throw new InvalidDataException("图片分块长度不匹配。");
        byte[] packet = Convert.FromBase64String(envelope);
        if (packet.Length != length + 28) throw new InvalidDataException("图片分块不完整。");
        using var cipher = ClipboardWire.DirectionalCipher(key, sender, Domain);
        cipher.Decrypt(packet.AsSpan(0, 12), packet.AsSpan(12, length), packet.AsSpan(12 + length),
            destination.AsSpan(index * ChunkSize, length), Header(sender, id, sentAt, totalSize, index));
    }
}
