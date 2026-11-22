using IndevoltCli.Ble;

namespace IndevoltCli;

/// <summary>Entry point: argument parsing and CLI verb dispatch.</summary>
public static class Program
{
    /// <summary>CLI entry. Exit code 0 = success, 1 = failure (the protocol code is printed on failure).</summary>
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Interrupted.");
        };
        try
        {
            return await DispatchAsync(args, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }
        switch (args[0].ToLowerInvariant())
        {
            case "help" or "h" or "--help" or "-h" or "?":
                PrintHelp();
                return 0;
            case "list":
                return await Commands.ListAsync(GetInt(args, "--seconds", Constants.DefaultScanSeconds), ct);
            case "config":
                return await ConfigAsync(args, ct);
            case "version":
            {
                var sn = FirstPositional(args);
                if (sn is null)
                {
                    Usage("version");
                    return 1;
                }
                return await Commands.VersionAsync(sn, GetDevicePassword(args), GetInt(args, "--scan-timeout", Constants.DefaultConnectScanSeconds), ct);
            }
            case "info":
            {
                var sn = FirstPositional(args);
                if (sn is null)
                {
                    Usage("info");
                    return 1;
                }
                return await Commands.InfoAsync(sn, GetDevicePassword(args), GetInt(args, "--scan-timeout", Constants.DefaultConnectScanSeconds), ct);
            }
            case "send":
            {
                var sn = FirstPositional(args);
                var command = GetOption(args, "--command");
                if (sn is null || string.IsNullOrEmpty(command))
                {
                    Usage("send");
                    return 1;
                }
                return await Commands.SendAsync(sn, command, GetDevicePassword(args), GetInt(args, "--scan-timeout", Constants.DefaultConnectScanSeconds), ct);
            }
            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                PrintHelp();
                return 1;
        }
    }

    private static async Task<int> ConfigAsync(string[] args, CancellationToken ct)
    {
        var sn = FirstPositional(args);
        var ssid = GetOption(args, "--ssid");
        if (sn is null || string.IsNullOrEmpty(ssid))
        {
            Usage("config");
            return 1;
        }
        var options = new ConfigOptions
        {
            Serial = sn,
            Ssid = ssid,
            WifiPassword = GetOption(args, "--password") ?? string.Empty,
            DevicePassword = GetDevicePassword(args),
            MobileData = HasFlag(args, "--mobile-data"),
            ScanSeconds = GetInt(args, "--scan-timeout", Constants.DefaultConnectScanSeconds),
        };
        return await Commands.ConfigAsync(options, ct);
    }

    private static string GetDevicePassword(string[] args) =>
        GetOption(args, "--device-password") ?? Constants.DefaultDevicePassword;

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == name && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    private static int GetInt(string[] args, string name, int fallback) =>
        int.TryParse(GetOption(args, name), out var value) ? value : fallback;

    private static bool HasFlag(string[] args, string name) => args.Contains(name);

    private static string? FirstPositional(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                return args[i];
        }
        return null;
    }

    private static void Usage(string verb)
    {
        Console.Error.WriteLine(verb switch
        {
            "config" => "usage: IndevoltCli config <sn> --ssid S [--password P] [--device-password DP] [--mobile-data] [--scan-timeout N]",
            "version" => "usage: IndevoltCli version <sn> [--device-password DP] [--scan-timeout N]",
            "info" => "usage: IndevoltCli info <sn> [--device-password DP] [--scan-timeout N]",
            "send" => "usage: IndevoltCli send <sn> --command \"AT+...\" [--device-password DP] [--scan-timeout N]",
            _ => "usage: IndevoltCli list | config | version | info | send | help",
        });
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            IndevoltCli — talk to an Indevolt (Solarman/IGene) battery over BLE AT (docs/PROTOCOL.md)

            Usage:
              IndevoltCli list [--seconds N]
                  Scan for BLE devices (default 10 s). Prints Name | Address | RSSI | ADV flag | SN.

              IndevoltCli config <sn> --ssid S [--password P] [--device-password DP]
                              [--mobile-data] [--scan-timeout N]
                  Full Wi-Fi configuration flow: scan, connect, AT+VER, [AT+IGDEVAUTH],
                  AT+IGTNETT, AT+CONFIG. P empty = open network (sent as "none").
                  DP defaults to the factory password. Exit 0 only on result code
                  1000 (or 1002/1003/1004, already configured).

              IndevoltCli version <sn> [--device-password DP] [--scan-timeout N]
                  Connect and send AT+VER; prints the raw reply.

              IndevoltCli info <sn> [--device-password DP] [--scan-timeout N]
                  Connect and read everything the AT channel offers:
                  AT+VER, AT+DTYPE, AT+WANN, AT+YZNETCHECK, AT+yzapp=214028,read.
                  Commands the device does not support are reported as
                  "no reply" and skipped.

              IndevoltCli send <sn> --command "AT+DTYPE" [--device-password DP]
                              [--scan-timeout N]
                  Send one raw AT command; prints the raw reply. \r\n is appended if missing.

              IndevoltCli help
            """);
    }
}
