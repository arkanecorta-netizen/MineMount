using System;
using System.Runtime.InteropServices;

namespace MineMount.Services;

/// <summary>
/// Cifrado local con DPAPI (ámbito usuario actual) para los tokens de
/// Microsoft. Sin paquetes extra: P/Invoke a crypt32.
/// </summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static byte[] Protect(byte[] plain)
    {
        var input = new DATA_BLOB { cbData = plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.pbData, plain.Length);
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new InvalidOperationException("DPAPI Protect falló");
            try
            {
                var cipher = new byte[output.cbData];
                Marshal.Copy(output.pbData, cipher, 0, output.cbData);
                return cipher;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    public static byte[] Unprotect(byte[] cipher)
    {
        var input = new DATA_BLOB { cbData = cipher.Length, pbData = Marshal.AllocHGlobal(cipher.Length) };
        try
        {
            Marshal.Copy(cipher, 0, input.pbData, cipher.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new InvalidOperationException("DPAPI Unprotect falló");
            try
            {
                var plain = new byte[output.cbData];
                Marshal.Copy(output.pbData, plain, 0, output.cbData);
                return plain;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }
}
