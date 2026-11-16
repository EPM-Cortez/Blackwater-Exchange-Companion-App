using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BlackwaterExchange;

/// <summary>
/// The app's device key, encrypted with Windows' data protection (DPAPI) for the signed-in Windows user, so the
/// file is useless copied to another account or PC. Only the website's hash of the key can be checked against it.
/// </summary>
public sealed class DeviceKey(string folder)
{
    // Ties the encrypted blob to this app, so another program using DPAPI for the same user cannot be tricked
    // into decrypting it by accident.
    private static readonly byte[] Entropy = "Blackwater Exchange device key v1"u8.ToArray();
    private static readonly Uri DefaultServer = ApiClient.ParseServer(AppSettings.DefaultServer);

    private string PathName => System.IO.Path.Combine(folder, "device.key");

    public bool Exists => File.Exists(PathName);

    public string? Read(Uri server)
    {
        if (!File.Exists(PathName))
            return null;
        try
        {
            var value = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(PathName)));
            var delimiter = value.IndexOf('\n');
            // The original app stored a bare token. Trust it only for the original production service.
            if (delimiter < 0)
                return ApiClient.IsSameOrigin(DefaultServer, server) ? value : null;
            return string.Equals(value[..delimiter], server.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)
                ? value[(delimiter + 1)..] : null;
        }
        catch (Win32Exception)
        {
            // Encrypted for another Windows user or PC: the app has to be linked again.
            return null;
        }
    }

    public void Save(string key, Uri server)
    {
        Directory.CreateDirectory(folder);
        AppStorage.WriteAtomically(PathName, Protect(Encoding.UTF8.GetBytes($"{server.GetLeftPart(UriPartial.Authority)}\n{key}")));
    }

    public void Delete()
    {
        if (File.Exists(PathName))
            File.Delete(PathName);
    }

    internal static byte[] Protect(byte[] data) => Crypt(data, protect: true);
    internal static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = Blob.From(data);
        var entropy = Blob.From(Entropy);
        var output = default(Blob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero)
                LocalFree(output.Data);
        }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;

        public static Blob From(byte[] bytes)
        {
            var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1)) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
