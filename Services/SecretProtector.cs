using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Qlyptra.Services;

/// <summary>Protects a secret for the current Windows user using DPAPI.</summary>
public static class SecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!OperatingSystem.IsWindows()) return "";
        var bytes = Encoding.UTF8.GetBytes(value);
        var inputPtr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, inputPtr, bytes.Length);
            var input = new DataBlob { Length = bytes.Length, Data = inputPtr };
            if (!CryptProtectData(ref input, "Qlyptra Yandex Music token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var encrypted = new byte[output.Length];
                Marshal.Copy(output.Data, encrypted, 0, output.Length);
                return Convert.ToBase64String(encrypted);
            }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(inputPtr); CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Unprotect(string encryptedValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue) || !OperatingSystem.IsWindows()) return "";
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(encryptedValue); }
        catch { return ""; }
        var inputPtr = Marshal.AllocHGlobal(encrypted.Length);
        try
        {
            Marshal.Copy(encrypted, 0, inputPtr, encrypted.Length);
            var input = new DataBlob { Length = encrypted.Length, Data = inputPtr };
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out var output)) return "";
            try
            {
                var bytes = new byte[output.Length];
                Marshal.Copy(output.Data, bytes, 0, output.Length);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(inputPtr); CryptographicOperations.ZeroMemory(encrypted); }
    }
}
