using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ShuangpinLink;

internal static class ClipboardWire
{
    const string Domain = "ime-sync/v2/clip";
    internal const string Owner = "owner";
    internal const string Peer = "peer";
    internal const int TextLimit = 64 * 1024;
    internal const long Lifetime = 600_000;
    internal const long AllowedClockSkew = 120_000;
    const int NonceSize = 12;
    const int TagSize = 16;

    internal static string Seal(string key, string sender, string id, long sentAt, ReadOnlySpan<byte> body)
    {
        if (!ValidId(id) || sentAt <= 0 || body.Length is < 1 or > TextLimit)
            throw new InvalidDataException("剪贴板消息格式无效。");
        byte[] frame = new byte[NonceSize + body.Length + TagSize];
        RandomNumberGenerator.Fill(frame.AsSpan(0, NonceSize));
        using var cipher = DirectionalCipher(key, sender);
        cipher.Encrypt(frame.AsSpan(0, NonceSize), body, frame.AsSpan(NonceSize, body.Length),
            frame.AsSpan(NonceSize + body.Length), AssociatedData(sender, id, sentAt));
        return Convert.ToBase64String(frame);
    }

    internal static byte[] Open(string key, string sender, string expectedSender, string id, long sentAt,
        string envelope, long now, long lastAcceptedAt)
    {
        if (!StringComparer.Ordinal.Equals(sender, expectedSender) || !ValidId(id)
            || sentAt <= 0 || sentAt <= lastAcceptedAt || sentAt <= now - Lifetime || sentAt > now + AllowedClockSkew
            || envelope.Length > (TextLimit + NonceSize + TagSize + 2) / 3 * 4)
            throw new InvalidDataException("剪贴板消息的来源或有效期无效。");

        byte[] frame = Convert.FromBase64String(envelope);
        int size = frame.Length - NonceSize - TagSize;
        if (size is < 1 or > TextLimit) throw new InvalidDataException("剪贴板消息长度无效。");
        byte[] plain = new byte[size];
        try
        {
            using var cipher = DirectionalCipher(key, sender);
            cipher.Decrypt(frame.AsSpan(0, NonceSize), frame.AsSpan(NonceSize, size),
                frame.AsSpan(NonceSize + size), plain, AssociatedData(sender, id, sentAt));
            return plain;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plain);
            throw;
        }
    }

    internal static bool ValidId(string id) => id.Length == 32 && id.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');

    static byte[] AssociatedData(string sender, string id, long sentAt) =>
        Encoding.UTF8.GetBytes($"{Domain}\n{sender}\n{id}\n{sentAt.ToString(CultureInfo.InvariantCulture)}");

    internal static AesGcm DirectionalCipher(string key, string sender, string domain = Domain)
    {
        string direction = sender switch
        {
            Owner => "owner-to-peer",
            Peer => "peer-to-owner",
            _ => throw new InvalidDataException("未知的剪贴板发送设备。")
        };
        byte[] shared = Convert.FromBase64String(key);
        try
        {
            if (shared.Length != 32) throw new CryptographicException("设备加密密钥无效。");
            byte[] derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32,
                Encoding.UTF8.GetBytes(domain), Encoding.UTF8.GetBytes(direction));
            try { return new AesGcm(derived, TagSize); }
            finally { CryptographicOperations.ZeroMemory(derived); }
        }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }
}
