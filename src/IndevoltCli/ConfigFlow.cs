using IndevoltCli.At;
using IndevoltCli.Ble;

namespace IndevoltCli;

/// <summary>Result codes reported to the UI layer (PROTOCOL.md §4 table).</summary>
public static class ResultCode
{
    /// <summary>Wi-Fi config success (AT+CONFIG completed, <c>+ok=connected</c>).</summary>
    public const int Success = 1000;
    /// <summary>Device already configured/in use.</summary>
    public const int AlreadyInUse = 1002;
    /// <summary>Already fully configured (nothing to do).</summary>
    public const int AlreadyConfigured = 1003;
    /// <summary>Success via alternate (AP) sub-flow.</summary>
    public const int AltSubFlow = 1004;
    /// <summary>Timed out (mapped to -1020 for CONFIG).</summary>
    public const int Timeout = -1;
    /// <summary>GATT connection lost / connect failed.</summary>
    public const int GattFailed = -1001;
    /// <summary>Device not found in scan.</summary>
    public const int DeviceNotFound = -1003;
    /// <summary>AT+CONFIG timeout (60 s without <c>+ok=connected</c>).</summary>
    public const int ConfigTimeout = -1020;
    /// <summary>Device password empty (auth-required device).</summary>
    public const int PasswordEmpty = -3000;
    /// <summary>Device password invalid format (auth-required device).</summary>
    public const int PasswordInvalid = -3001;
    /// <summary>AT+VER auth-probe failed.</summary>
    public const int VersionProbeFailed = -3002;
}

/// <summary>Options for the <c>config</c> verb.</summary>
public sealed record ConfigOptions
{
    /// <summary>Device serial number (as printed on the unit; leading-zero rule applies).</summary>
    public required string Serial { get; init; }

    /// <summary>Target Wi-Fi SSID (sanitized before sending).</summary>
    public required string Ssid { get; init; }

    /// <summary>Target Wi-Fi password; empty means open network (sent as literal <c>none</c>).</summary>
    public required string WifiPassword { get; init; }

    /// <summary>Device (auth) password; defaults to the factory password.</summary>
    public string DevicePassword { get; init; } = Ble.Constants.DefaultDevicePassword;

    /// <summary>true → <c>AT+IGTNETT=214028,0,0</c> (mobile data); false → <c>214028,1,1</c> (Wi-Fi only).</summary>
    public bool MobileData { get; init; }

    /// <summary>Scan window in seconds before giving up on finding the device.</summary>
    public int ScanSeconds { get; init; } = Ble.Constants.DefaultScanSeconds;
}

/// <summary>
/// The master configuration sequence from PROTOCOL.md §3: scan for AP_&lt;SN&gt;,
/// parse the ADV flag, connect, MTU 203, verify 0000fee7, enable notifications,
/// AT+VER, [AT+IGDEVAUTH], AT+IGTNETT, AT+CONFIG.
/// </summary>
public sealed class ConfigFlow
{
    private static readonly string[] OkMarkers = ["+ok", "+OK"];
    private static readonly string[] ErrMarkers = ["+err", "+ERR"];
    private static readonly string[] ConfigFailMarkers = ["+err", "ssid error", "pass error"];

    /// <summary>Runs the full flow; returns the protocol result code (1000 = success).</summary>
    public async Task<int> RunAsync(ConfigOptions o, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(o.DevicePassword))
        {
            Fail($"device password is empty (auth-required device) (code {ResultCode.PasswordEmpty})");
            return ResultCode.PasswordEmpty;
        }
        if (o.DevicePassword.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            Fail($"device password has invalid characters (code {ResultCode.PasswordInvalid})");
            return ResultCode.PasswordInvalid;
        }

        // 1. Scan for AP_<SN>, parse ADV flag.
        var target = BatteryScanner.TargetName(o.Serial);
        // Fire the device-store snapshot in parallel with the scan (it must
        // never delay the connect; the constructed-id fallback covers a
        // missing entry).
        _ = Battery.PrepareDeviceStoreAsync(ct);
        Step($"[1/6] scanning for {target} (up to {o.ScanSeconds}s)...");
        ScannedDevice? found;
        using (var scanner = new BatteryScanner())
        {
            var seen = await scanner.ScanAsync(o.ScanSeconds, target, ct);
            found = seen.FirstOrDefault(d => d.Name == target);
        }
        if (found is null)
        {
            Fail($"device not found (code {ResultCode.DeviceNotFound})");
            return ResultCode.DeviceNotFound;
        }
        Ok($"found {found.Name} @ {found.DisplayAddress} (RSSI {found.Rssi} dBm, ADV {AdvParser.DescribeFlag(found.Flag)})");

        // 2. 100 ms delay, then connect (up to 3 attempts).
        await Task.Delay(Constants.StepDelay, ct);

        Battery? battery = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                battery = await Battery.ConnectAsync(found.Address, ct);
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Step($"connect attempt {attempt}/3 failed: {ex.Message}");
                await Task.Delay(Constants.StepDelay, ct);
            }
        }
        if (battery is null)
        {
            Fail($"GATT connect failed (code {ResultCode.GattFailed})");
            return ResultCode.GattFailed;
        }

        using (battery)
        {
            Step("[2/6] connected; AT channel service 0000fee7 present; notifications enabled on fec8/fed4; MTU negotiated by Windows (protocol wants 203)");

            var session = new AtSession(battery, found.Flag, o.DevicePassword);

            // 3. AT+VER — always the first command on the channel.
            Step("[3/6] AT+VER...");
            var ver = await session.SendAsync("AT+VER", OkMarkers, ErrMarkers, Constants.VersionTimeout, ct);
            if (!ver.Success)
            {
                Fail($"AT+VER → {Text(ver)} (code {ResultCode.VersionProbeFailed})");
                return ResultCode.VersionProbeFailed;
            }
            Ok($"AT+VER → {ver.Reply}");

            // 4. Auth handshake, only when the device advertises encryption (password mode).
            if (session.IsEncrypted)
            {
                await Task.Delay(Constants.StepDelay, ct);
                var auth = $"AT+IGDEVAUTH={Constants.ProductId},7,1";
                Step($"[4/6] {auth} (encrypted session)...");
                var result = await session.SendAsync(auth, OkMarkers, ErrMarkers, Constants.AuthTimeout, ct);
                if (!result.Success)
                {
                    Fail($"{auth} → {Text(result)} (code {ResultCode.Timeout})");
                    return ResultCode.Timeout;
                }
                Ok($"{auth} → {result.Reply}");
            }
            else
            {
                Step("[4/6] ADV flag '0' (plaintext): AT+IGDEVAUTH not required");
            }

            // 5. Data policy: Wi-Fi only by default, mobile data with --mobile-data.
            await Task.Delay(Constants.StepDelay, ct);
            var net = o.MobileData
                ? $"AT+IGTNETT={Constants.ProductId},0,0"
                : $"AT+IGTNETT={Constants.ProductId},1,1";
            Step($"[5/6] {net}...");
            var nett = await session.SendAsync(net, OkMarkers, ErrMarkers, Constants.NetTimeout, ct);
            if (!nett.Success)
            {
                Fail($"{net} → {Text(nett)} (code {ResultCode.Timeout})");
                return ResultCode.Timeout;
            }
            Ok($"{net} → {nett.Reply}");

            // 6. Assign the Wi-Fi network (60 s: the device joins the network).
            await Task.Delay(Constants.StepDelay, ct);
            var ssid = SanitizeSsid(o.Ssid);
            if (ssid.Length == 0)
            {
                Fail($"SSID is empty after sanitizing to allowed characters (code {ResultCode.Timeout})");
                return ResultCode.Timeout;
            }
            var wifiPwd = string.IsNullOrEmpty(o.WifiPassword) ? "none" : o.WifiPassword;
            var config = $"AT+CONFIG={ssid},{wifiPwd}";
            Step($"[6/6] {config} (60 s — the device joins the network)...");
            var conf = await session.SendAsync(config, ["+ok=connected"], ConfigFailMarkers, Constants.ConfigTimeout, ct);
            if (conf.Success)
            {
                Ok($"{config} → {conf.Reply} (code {ResultCode.Success})");
                return ResultCode.Success;
            }
            if (Text(conf).Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                Ok($"{config} → {Text(conf)} (device already configured; code {ResultCode.AlreadyConfigured})");
                return ResultCode.AlreadyConfigured;
            }
            var code = conf.RawReply is null ? ResultCode.ConfigTimeout : ResultCode.Timeout;
            Fail($"{config} → {Text(conf)} (code {code})");
            return code;
        }
    }

    /// <summary>
    /// Keeps only <c>[A-Za-z0-9!@#$%^&amp;*()-_=+.,?~]</c> in the SSID
    /// (special characters stripped before sending, PROTOCOL.md §4).
    /// </summary>
    public static string SanitizeSsid(string ssid)
    {
        const string Allowed = "A-Za-z0-9!@#$%^&*()-_=+.,?~";
        return string.Concat(ssid.Where(c => Allowed.Contains(c, StringComparison.Ordinal)));
    }

    private static void Step(string message) => Console.WriteLine(message);

    private static void Ok(string message) => Console.WriteLine($"[OK] {message}");

    private static void Fail(string message) => Console.WriteLine($"[FAIL] {message}");

    private static string Text(AtResult r) => r.Reply ?? "(no reply)";
}
