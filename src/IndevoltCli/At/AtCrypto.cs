using System.Security.Cryptography;
using System.Text;
using IndevoltCli.Ble;

namespace IndevoltCli.At;

/// <summary>
/// AES-CBC/PKCS7 with key == IV, and the two key derivations of PROTOCOL.md §5.
/// The key is always a 16-character ASCII hex string.
/// </summary>
public static class AtCrypto
{
    /// <summary>AES-CBC encrypt (or decrypt, <paramref name="encrypt"/>) with key == IV.</summary>
    public static byte[] Transform(byte[] data, string key, bool encrypt)
    {
        if (key is null || key.Length != 16)
            throw new ArgumentException("Key must be exactly 16 characters.", nameof(key));

        var keyBytes = Encoding.ASCII.GetBytes(key);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = keyBytes;
        aes.IV = keyBytes;
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>AES-CBC/PKCS7 encrypt with key == IV.</summary>
    public static byte[] Encrypt(byte[] data, string key) => Transform(data, key, encrypt: true);

    /// <summary>AES-CBC/PKCS7 decrypt with key == IV.</summary>
    public static byte[] Decrypt(byte[] data, string key) => Transform(data, key, encrypt: false);

    /// <summary>
    /// AES-CBC decrypt WITHOUT padding (the app's <c>o()</c> uses
    /// AES/CBC/NoPadding on replies), key == IV. Caller strips any trailing
    /// padding/NULs.
    /// </summary>
    public static byte[] DecryptNoPad(byte[] data, string key)
    {
        if (key is null || key.Length != 16)
            throw new ArgumentException("Key must be exactly 16 characters.", nameof(key));
        var keyBytes = Encoding.ASCII.GetBytes(key);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = keyBytes;
        aes.IV = keyBytes;
        using var transform = aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>
    /// ADV flag '2' derivation: hex of the char codes of the device
    /// password, truncated to 16 hex chars (right-padded with '0' if shorter).
    /// </summary>
    public static string DeriveKeyFlag2(string devicePassword)
    {
        var hex = new StringBuilder(devicePassword.Length * 2);
        foreach (var ch in devicePassword)
            hex.Append(((int)ch).ToString("X2"));
        return Normalize(hex.ToString());
    }

    /// <summary>
    /// ADV flag '1' derivation, exactly as PROTOCOL.md §5:
    /// s = "214028" + password (truncated to 15 chars) → byte-wise 0xFF-b →
    /// MD5 → byte-wise 0xFF-b → 4x4 matrix (row-major), XOR of each column →
    /// 8-char lowercase hex H → key = hex of the ASCII codes of H (16 chars).
    /// (The final hex-encode is the same transform the flag '2' derivation
    /// applies to the password.)
    /// </summary>
    public static string DeriveKeyFlag1(string password)
    {
        var pwd = password.Length > 15 ? password[..15] : password;
        var s = Constants.ProductId + pwd;
        var step1 = Encoding.ASCII.GetBytes(s).Select(b => (byte)(0xFF - b)).ToArray();
        var hash = MD5.HashData(step1);
        var inv = hash.Select(b => (byte)(0xFF - b)).ToArray();

        Span<byte> columns = stackalloc byte[4];
        for (int c = 0; c < 4; c++)
        {
            byte x = 0;
            for (int r = 0; r < 4; r++)
                x ^= inv[r * 4 + c]; // row-major 4x4 layout
            columns[c] = x;
        }
        var xorHex = Convert.ToHexString(columns).ToLowerInvariant(); // 8 chars
        var key = new StringBuilder(xorHex.Length * 2);
        foreach (var ch in xorHex)
            key.Append(((int)ch).ToString("X2"));
        return Normalize(key.ToString()); // exactly 16 chars
    }

    private static string Normalize(string hex)
    {
        if (hex.Length > 16)
            hex = hex[..16];
        if (hex.Length < 16)
            hex = hex.PadRight(16, '0');
        return hex;
    }
}
