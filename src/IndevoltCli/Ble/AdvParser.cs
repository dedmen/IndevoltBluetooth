using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth.Advertisement;

namespace IndevoltCli.Ble;

/// <summary>
/// Parses the legacy advertising payload for the encryption flag, per PROTOCOL.md §2.
///
/// The wire format is a sequence of AD structures <c>[len, type, data...]</c>.
/// The device puts its flag into a manufacturer-specific structure
/// <c>[len, 0xFF, companyLo, companyHi, flag, ...]</c>.
///
/// Note: this WinRT projection hands out the advertisement as structured
/// sections instead of raw TLV bytes, so <see cref="ParseFlagFromManufacturerData"/>
/// is the path used at runtime; <see cref="ParseFlagFromTlv"/> walks the raw
/// TLV layout and is kept for completeness (and unit-testability).
/// </summary>
public static class AdvParser
{
    /// <summary>AD type: manufacturer specific data.</summary>
    public const byte ManufacturerSpecific = 0xFF;

    /// <summary>AD type: short local name.</summary>
    public const byte ShortLocalName = 0x08;

    /// <summary>AD type: complete local name.</summary>
    public const byte CompleteLocalName = 0x09;

    /// <summary>
    /// Walks a raw legacy advertising payload <c>[len, type, data...]</c> and
    /// returns the flag of the first long-enough manufacturer-specific structure:
    /// <c>[len, 0xFF, companyLo, companyHi, flag, ...]</c>. Null when absent.
    /// </summary>
    public static byte? ParseFlagFromTlv(byte[]? rawData)
    {
        if (rawData is null || rawData.Length < 4)
            return null;

        int i = 0;
        while (i + 1 < rawData.Length)
        {
            int len = rawData[i]; // covers the type byte + payload
            if (len == 0)
                break;
            if (i + len >= rawData.Length)
                break; // truncated structure
            byte type = rawData[i + 1];
            if (type == ManufacturerSpecific && len >= 4) // payload >= 3: company(2) + flag(1)
            {
                return rawData[i + 4];
            }
            i += len + 1;
        }
        return null;
    }

    /// <summary>
    /// Walks a raw legacy advertising payload <c>[len, type, data...]</c> and
    /// returns the advertised local name (AD type 0x09, falling back to 0x08).
    /// Windows often leaves <c>Advertisement.LocalName</c> empty in scan
    /// results; the name is still present in the raw AD structures.
    /// Null when no name structure is present.
    /// </summary>
    public static string? ParseNameFromTlv(byte[]? rawData)
    {
        if (rawData is null)
            return null;

        string? shortName = null;
        int i = 0;
        while (i + 1 < rawData.Length)
        {
            int len = rawData[i]; // covers the type byte + payload
            if (len == 0)
                break;
            if (i + len >= rawData.Length)
                break; // truncated structure
            byte type = rawData[i + 1];
            if (type == CompleteLocalName && len >= 2)
                return Encoding.UTF8.GetString(rawData, i + 2, len - 1);
            if (type == ShortLocalName && len >= 2)
                shortName ??= Encoding.UTF8.GetString(rawData, i + 2, len - 1);
            i += len + 1;
        }
        return shortName;
    }

    /// <summary>
    /// Returns the advertised local name from the raw AD sections of a
    /// <see cref="BluetoothLEAdvertisement"/>: a section of <see cref="CompleteLocalName"/>
    /// (0x09), falling back to <see cref="ShortLocalName"/> (0x08). Windows often
    /// leaves <c>Advertisement.LocalName</c> empty in scan results even though
    /// the name is present in the raw sections. Null when neither is found.
    /// </summary>
    public static string? ParseNameFromSections(IList<BluetoothLEAdvertisementDataSection>? sections)
    {
        if (sections is null)
            return null;

        string? shortName = null;
        foreach (var section in sections)
        {
            if (section.Data is null)
                continue;
            var bytes = section.Data.ToArray();
            if (bytes.Length == 0)
                continue;
            if (section.DataType == CompleteLocalName)
                return Encoding.UTF8.GetString(bytes);
            if (section.DataType == ShortLocalName)
                shortName ??= Encoding.UTF8.GetString(bytes);
        }
        return shortName;
    }

    /// <summary>
    /// Returns the flag from a manufacturer-specific AD section's data
    /// <c>[companyLo, companyHi, flag, ...]</c> (what the WinRT section APIs deliver),
    /// or null when the payload is too short.
    /// </summary>
    public static byte? ParseFlagFromManufacturerData(byte[]? data)
    {
        if (data is null || data.Length < 3)
            return null;
        return data[2];
    }

    /// <summary>Human-readable description of a flag (PROTOCOL.md §2).</summary>
    public static string DescribeFlag(byte? flag) => flag switch
    {
        (byte)'0' => "plaintext",
        (byte)'1' => "crypto (MD5 key)",
        (byte)'2' => "crypto (device-password key)",
        _ => "unknown",
    };
}
