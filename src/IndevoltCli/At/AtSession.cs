using System.Security.Cryptography;
using System.Text;
using IndevoltCli.Ble;

namespace IndevoltCli.At;

/// <summary>The outcome of one AT command exchange.</summary>
public readonly record struct AtResult(bool Success, string? Matched, string? RawReply)
{
    /// <summary>The reply text to show the user (the marker match, else the first reply seen); null on silence.</summary>
    public string? Reply => Matched ?? RawReply;
}

/// <summary>
/// The command layer over a <see cref="Battery"/>: encodes (and, per the ADV
/// flag, AES-encrypts) outgoing commands, decodes incoming notifications, and
/// matches them against success/failure marker lists. Matching is
/// case-insensitive containment, checked notification by notification in
/// arrival order; success markers are tested before failure markers.
/// </summary>
public sealed class AtSession
{
    private readonly Battery _battery;
    private readonly string? _key;

    /// <summary>The ADV flag in effect: '0' plaintext, '1' MD5 key, '2' device-password key.</summary>
    public byte Flag { get; }

    /// <summary>True when this session encrypts commands and decrypts replies.</summary>
    public bool IsEncrypted => _key is not null;

    /// <summary>Creates a session; a null <paramref name="advFlag"/> is treated as '0' (plaintext).</summary>
    public AtSession(Battery battery, byte? advFlag, string devicePassword)
    {
        _battery = battery;
        Flag = advFlag is byte f && f != (byte)'0' ? f : (byte)'0';
        _key = Flag switch
        {
            (byte)'1' => AtCrypto.DeriveKeyFlag1(devicePassword),
            (byte)'2' => AtCrypto.DeriveKeyFlag2(devicePassword),
            _ => null,
        };
    }

    /// <summary>
    /// Writes the command (UTF-8, <c>\r\n</c>-terminated, encrypted per the ADV flag)
    /// to fec7 and waits for the first notification matching a marker.
    /// No marker hit before <paramref name="timeout"/> is a failure.
    /// </summary>
    public async Task<AtResult> SendAsync(
        string command,
        IReadOnlyList<string> successMarkers,
        IReadOnlyList<string> failureMarkers,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        await _battery.WriteAsync(BuildPayload(command));
        long startSeq = _battery.MaxSeq;
        return await WaitForAsync(startSeq, successMarkers, failureMarkers, timeout, ct);
    }

    /// <summary>
    /// Writes the command and returns the first notification after it that
    /// DECODES to text (the device may push unrelated encrypted status blobs
    /// that this key cannot decrypt — those are skipped, as the app's
    /// handler does), or null on timeout / disconnect.
    /// </summary>
    public async Task<string?> QueryAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        await _battery.WriteAsync(BuildPayload(command));
        long startSeq = _battery.MaxSeq;
        long loggedSeq = startSeq;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (!_battery.IsConnected)
                    return null;
                foreach (var (seq, bytes, _) in _battery.Recent())
                {
                    if (seq <= startSeq)
                        continue;
                    if (TryDecode(bytes, out var text) && !string.IsNullOrEmpty(text))
                        return text;
                    // Report each undecryptable payload once, then keep
                    // waiting for the real reply.
                    if (seq > loggedSeq)
                    {
                        loggedSeq = seq;
                        Console.WriteLine($"[..] skipped undecryptable notification ({bytes.Length} bytes, seq {seq})");
                    }
                }
                await Task.Delay(50, cts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<AtResult> WaitForAsync(
        long startSeq,
        IReadOnlyList<string> success,
        IReadOnlyList<string> failure,
        TimeSpan timeout,
        CancellationToken ct)
    {
        string? raw = null;
        var successUp = success.Select(m => m.ToUpperInvariant()).Distinct().ToArray();
        var failureUp = failure.Select(m => m.ToUpperInvariant()).Distinct().ToArray();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (!_battery.IsConnected)
                    break;
                foreach (var (seq, bytes, _) in _battery.Recent())
                {
                    if (seq <= startSeq)
                        continue;
                    var text = Decode(bytes);
                    raw ??= text;
                    var up = text.ToUpperInvariant();
                    if (successUp.Any(m => up.Contains(m)))
                        return new AtResult(true, text, raw);
                    if (failureUp.Any(m => up.Contains(m)))
                        return new AtResult(false, text, raw);
                }
                await Task.Delay(50, cts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // timeout
        }
        return new AtResult(false, null, raw);
    }

    private byte[] BuildPayload(string command)
    {
        var text = command.EndsWith("\r\n", StringComparison.Ordinal) ? command : command + "\r\n";
        var plain = Encoding.UTF8.GetBytes(text);
        return _key is null ? plain : AtCrypto.Encrypt(plain, _key);
    }

    private string Decode(byte[] bytes)
        => TryDecode(bytes, out var text)
            ? text
            : "<undecryptable> " + Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Decrypts to text; false when the payload is not valid
    /// ciphertext for this key (unrelated status traffic). Tries PKCS7
    /// first, then NoPadding (the app's mode) accepting only output that
    /// looks like text.</summary>
    private bool TryDecode(byte[] bytes, out string text)
    {
        if (_key is null || IsMostlyPrintableBytes(bytes))
        {
            text = Encoding.UTF8.GetString(bytes);
            return true;
        }
        try
        {
            text = Encoding.UTF8.GetString(AtCrypto.Decrypt(bytes, _key));
            return true;
        }
        catch (CryptographicException)
        {
            // fall through to NoPadding
        }
        try
        {
            var raw = AtCrypto.DecryptNoPad(bytes, _key);
            var candidate = TrimTrailingGarbage(raw);
            if (candidate.Length >= Math.Max(1, raw.Length / 2) && IsMostlyPrintable(candidate))
            {
                text = candidate;
                return true;
            }
        }
        catch (CryptographicException)
        {
            // not this key either
        }
        text = string.Empty;
        return false;
    }

    /// <summary>Cuts trailing PKCS7/NUL padding garbage after a NoPadding decrypt.</summary>
    private static string TrimTrailingGarbage(byte[] raw)
    {
        var end = raw.Length;
        while (end > 0)
        {
            var b = raw[end - 1];
            if (b is >= 0x20 and <= 0x7e or 0x0d or 0x0a)
                break;
            end--;
        }
        return Encoding.UTF8.GetString(raw, 0, end);
    }

    private static bool IsMostlyPrintable(string s)
    {
        if (s.Length == 0)
            return false;
        var printable = s.Count(c => (c >= '\u0020' && c <= '\u007e') || c == '\r' || c == '\n');
        return (double)printable / s.Length >= 0.9;
    }

    private static bool IsMostlyPrintableBytes(byte[] b)
    {
        if (b.Length == 0)
            return false;
        var printable = b.Count(c => c is >= 0x20 and <= 0x7e or 0x0d or 0x0a);
        return (double)printable / b.Length >= 0.9;
    }
}
