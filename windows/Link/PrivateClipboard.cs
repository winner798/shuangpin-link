using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ShuangpinLink;

internal static class PrivateClipboard
{
    const string ExclusionFormat = "ExcludeClipboardContentFromMonitorProcessing";
    const uint UnicodeText = 13;
    const uint MoveableMemory = 0x0002;

    internal static uint Sequence => GetClipboardSequenceNumber();

    internal static bool IsProtected(IDataObject data) => data.GetDataPresent(ExclusionFormat, false)
        || data.GetDataPresent("CanUploadToCloudClipboard", false)
        || data.GetDataPresent("CanIncludeInClipboardHistory", false);

    internal static bool Write(nint window, uint sequence, string text)
    {
        byte[] encoded = new byte[(text.Length + 1) * sizeof(char)];
        try
        {
            Encoding.Unicode.GetBytes(text.AsSpan(), encoded);
            return WriteFormats(window, sequence, [(UnicodeText, encoded)]);
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    internal static bool WriteFormats(nint window, uint sequence, List<(uint Format, byte[] Value)> formats)
    {
        formats.Insert(0, (RegisterClipboardFormatW(ExclusionFormat), [1, 0, 0, 0]));
        var handles = new nint[formats.Count];
        try
        {
            for (int i = 0; i < handles.Length; i++)
            {
                if (formats[i].Format == 0) throw new ExternalException();
                handles[i] = Allocate(formats[i].Value);
            }
            if (!OpenClipboard(window)) throw new ExternalException();
            try
            {
                // Check for a local copy under the same clipboard lock used to replace it.
                if (sequence != Sequence) return false;
                if (!EmptyClipboard()) throw new ExternalException();
                for (int i = 0; i < handles.Length; i++)
                {
                    if (SetClipboardData(formats[i].Format, handles[i]) == 0) throw new ExternalException();
                    handles[i] = 0;
                }
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            foreach (nint memory in handles) if (memory != 0) GlobalFree(memory);
        }
    }

    static nint Allocate(byte[] bytes)
    {
        nint memory = GlobalAlloc(MoveableMemory, (nuint)bytes.Length);
        if (memory == 0) throw new ExternalException();
        nint address = GlobalLock(memory);
        if (address == 0) { GlobalFree(memory); throw new ExternalException(); }
        try { Marshal.Copy(bytes, 0, address, bytes.Length); }
        finally { GlobalUnlock(memory); }
        return memory;
    }

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);
}
