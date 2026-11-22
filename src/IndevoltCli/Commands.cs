using System.Text;
using IndevoltCli.At;
using IndevoltCli.Ble;

namespace IndevoltCli;

/// <summary>The CLI verbs: list, config, version, send.</summary>
public static class Commands
{
    /// <summary>
    /// <c>list [--seconds N]</c> — scan and print each device as soon as it
    /// appears (the battery duty-cycles its advertising, so it can take a
    /// while; the scan keeps listening until the window ends).
    /// </summary>
    public static async Task<int> ListAsync(int seconds, CancellationToken ct)
    {
        Console.WriteLine($"Scanning for {seconds}s — devices are printed as they appear...");
        using var scanner = new BatteryScanner();
        var reported = 0;
        scanner.DeviceFound += d =>
        {
            Console.WriteLine("  " + DescribeFound(d));
            Interlocked.Increment(ref reported);
        };
        var devices = await scanner.ScanAsync(seconds, null, ct);

        // Watcher-unavailable fallback: nothing was reported live.
        if (reported == 0)
        {
            foreach (var d in devices)
                Console.WriteLine("  " + DescribeFound(d));
        }

        if (devices.Count == 0)
            Console.WriteLine("No BLE devices seen. (Is Bluetooth on? On Windows, scanning may need location permission.)");
        else
            Console.WriteLine($"Scan complete: {devices.Count} device(s) seen.");
        return 0;
    }

    private static string DescribeFound(ScannedDevice d)
    {
        var name = string.IsNullOrEmpty(d.Name) ? "(no name)" : d.Name;
        return $"{name.PadRight(20)} {d.DisplayAddress}  {d.Rssi} dBm  ADV {FlagText(d.Flag)}  SN {d.Serial ?? "-"}";
    }

    /// <summary><c>config</c> — run the master flow; exit 0 only on codes 1000/1002/1003/1004.</summary>
    public static async Task<int> ConfigAsync(ConfigOptions o, CancellationToken ct)
    {
        var code = await new ConfigFlow().RunAsync(o, ct);
        Console.WriteLine($"Result code: {code}");
        return code >= ResultCode.Success ? 0 : 1;
    }

    /// <summary><c>version</c> — connect and send AT+VER, printing the raw reply.</summary>
    public static async Task<int> VersionAsync(string serial, string devicePassword, int scanSeconds, CancellationToken ct)
    {
        // Fire the device-store snapshot in parallel with the scan: it feeds
        // the id resolution for the connect, but must never delay it (the
        // enumeration can hang up to its 15 s bound; the constructed-id
        // fallback covers a missing entry).
        _ = Battery.PrepareDeviceStoreAsync(ct);
        var link = await FindAndConnectAsync(serial, scanSeconds, ct);
        if (link is null)
            return 1;
        try
        {
            using var battery = link.Device;
            var session = new AtSession(battery, link.Flag, devicePassword);
            var reply = await session.QueryAsync("AT+VER", Constants.VersionTimeout, ct);
            if (reply is null)
            {
                Console.WriteLine($"[FAIL] AT+VER: no reply (code {ResultCode.Timeout})");
                return 1;
            }
            Console.WriteLine($"[OK] AT+VER reply: {Oneline(reply)}");
            return 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {ex.Message} (code {ResultCode.GattFailed})");
            return 1;
        }
    }

    /// <summary>
    /// <c>info</c> — connect and read everything the AT channel will tell us:
    /// AT+VER (firmware), AT+DTYPE (device type), AT+WANN (WAN/network status),
    /// AT+YZNETCHECK (cellular network check), AT+yzapp=214028,read (stored
    /// app config). Commands the device does not implement simply get no
    /// reply and are skipped. Exit 0 when at least one command answered.
    /// </summary>
    public static async Task<int> InfoAsync(string serial, string devicePassword, int scanSeconds, CancellationToken ct)
    {
        _ = Battery.PrepareDeviceStoreAsync(ct);
        var link = await FindAndConnectAsync(serial, scanSeconds, ct);
        if (link is null)
            return 1;
        try
        {
            using var battery = link.Device;
            var session = new AtSession(battery, link.Flag, devicePassword);

            var probes = new (string Label, string Command)[]
            {
                ("Firmware version", "AT+VER"),
                ("Device type", "AT+DTYPE"),
                ("WAN / network info", "AT+WANN"),
                ("Cellular network check", "AT+YZNETCHECK"),
                ("Stored app config", $"AT+yzapp={Constants.ProductId},read"),
            };

            var answered = 0;
            foreach (var (label, command) in probes)
            {
                var reply = await session.QueryAsync(command, Constants.VersionTimeout, ct);
                if (reply is not null)
                {
                    answered++;
                    Console.WriteLine($"[OK] {label} ({command}): {Oneline(reply)}");
                }
                else
                {
                    Console.WriteLine($"[--] {label} ({command}): no reply");
                }
            }

            Console.WriteLine(answered > 0
                ? "Read complete."
                : "[FAIL] no command answered (code " + ResultCode.Timeout + ")");
            return answered > 0 ? 0 : 1;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {ex.Message} (code {ResultCode.GattFailed})");
            return 1;
        }
    }

    /// <summary><c>send</c> — one raw AT command, raw reply printed; <c>\r\n</c> appended if missing.</summary>
    public static async Task<int> SendAsync(string serial, string command, string devicePassword, int scanSeconds, CancellationToken ct)
    {
        _ = Battery.PrepareDeviceStoreAsync(ct);
        var link = await FindAndConnectAsync(serial, scanSeconds, ct);
        if (link is null)
            return 1;
        try
        {
            using var battery = link.Device;
            var session = new AtSession(battery, link.Flag, devicePassword);
            var reply = await session.QueryAsync(command, Constants.VersionTimeout, ct);
            if (reply is null)
            {
                Console.WriteLine($"[FAIL] no reply to {command} (code {ResultCode.Timeout})");
                return 1;
            }
            Console.WriteLine($"[OK] reply: {reply}");
            return 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {ex.Message} (code {ResultCode.GattFailed})");
            return 1;
        }
    }

    private static async Task<ScannedDevice?> FindAsync(string serial, int scanSeconds, CancellationToken ct)
    {
        var target = BatteryScanner.TargetName(serial);
        Console.WriteLine($"Scanning for {target} (up to {scanSeconds}s)...");
        using var scanner = new BatteryScanner();
        var seen = await scanner.ScanAsync(scanSeconds, target, ct);
        var found = seen.FirstOrDefault(d => d.Name == target);
        if (found is null)
        {
            Console.WriteLine($"[FAIL] device not found (code {ResultCode.DeviceNotFound})");
            return null;
        }
        Console.WriteLine($"[OK] found {found.Name} @ {found.DisplayAddress} (RSSI {found.Rssi} dBm, ADV {AdvParser.DescribeFlag(found.Flag)})");
        return found;
    }

    /// <summary>A connected battery plus the ADV flag (encryption mode) from the advertisement that was caught.</summary>
    public sealed record BatteryLink(Battery Device, byte? Flag);

    /// <summary>
    /// Scans for the battery and connects, repeating the cycle when the
    /// connect fails. The battery only keeps a connection while its
    /// advertising burst is live — a connect that outlives the burst gets
    /// dropped during GATT discovery, and retrying the connect alone is
    /// futile until the NEXT burst, which only a fresh scan can catch.
    /// Round 1 uses the full scan window, retries use a shorter one.
    /// </summary>
    public static async Task<BatteryLink?> FindAndConnectAsync(string serial, int scanSeconds, CancellationToken ct)
    {
        const int rounds = 3;
        for (var round = 1; round <= rounds; round++)
        {
            var seconds = round == 1 ? scanSeconds : Math.Min(60, scanSeconds);
            var found = await FindAsync(serial, seconds, ct);
            if (found is null)
                return null;
            await Task.Delay(Constants.StepDelay, ct);
            try
            {
                return new BatteryLink(await Battery.ConnectAsync(found.Address, ct), found.Flag);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"[..] round {round}/{rounds}: {ex.Message}");
                Console.WriteLine("[..] re-scanning for the next advertising burst...");
            }
        }
        return null;
    }

    private static string FlagText(byte? flag) =>
        flag is byte f ? $"'{(char)f}' {AdvParser.DescribeFlag(f)}" : "?";

    private static string Oneline(string text) =>
        text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
}
