using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace IndevoltCli.Ble;

/// <summary>One observed advertisement for a BLE device (merged across ADV/SCAN events).</summary>
public sealed record ScannedDevice(string Name, string Address, int Rssi, byte? Flag)
{
    /// <summary>The serial number when the name has the <c>AP_&lt;SN&gt;</c> form, else null.</summary>
    public string? Serial =>
        !string.IsNullOrEmpty(Name) && Name.StartsWith("AP_", StringComparison.Ordinal)
            ? Name["AP_".Length..]
            : null;

    /// <summary>The address with colons, for display.</summary>
    public string DisplayAddress =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => Address[((i + 1) * 2 - 2)..((i + 1) * 2)]));
}

/// <summary>
/// Watches BLE advertisements for a number of seconds. Prefers
/// <see cref="BluetoothLEAdvertisementWatcher"/>; if the watcher cannot start
/// (typically no Bluetooth/location permission on Windows) it falls back to
/// enumerating known BLE devices, without live RSSI or ADV data.
/// </summary>
public sealed class BatteryScanner : IDisposable
{
    private static ulong _fallbackKey = 0x7F00_0000_0000_0001;

    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly ConcurrentDictionary<ulong, ScannedDevice> _seen = new();
    private Action<string, byte?>? _onAdvertised;
    private Dictionary<string, string> _cachedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised once, immediately, when a device first appears in the scan
    /// (name already filled from the advertisement or the Windows cache).
    /// </summary>
    public event Action<ScannedDevice>? DeviceFound;

    public BatteryScanner()
    {
        // ACTIVE scan (sends SCAN_REQUEST probes): the battery stays quiet
        // unless probed — the app's config flow uses SCAN_MODE_LOW_LATENCY,
        // and passive scanning misses most of its bursts.
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        _watcher.Received += OnReceived;
    }

    /// <summary>
    /// The target advertised name for a serial, with the leading-zero rule of
    /// PROTOCOL.md §2: one leading '0' is stripped (<c>0123</c> → <c>AP_123</c>).
    /// </summary>
    public static string TargetName(string serial)
    {
        var sn = serial.StartsWith('0') ? serial[1..] : serial;
        return "AP_" + sn;
    }

    /// <summary>
    /// Scans for <paramref name="seconds"/>. When <paramref name="targetName"/> is
    /// given the scan stops early as soon as that name is seen carrying a parsed
    /// ADV flag (name and flag may arrive on separate advertisements).
    /// </summary>
    public async Task<IReadOnlyList<ScannedDevice>> ScanAsync(int seconds, string? targetName = null, CancellationToken ct = default)
    {
        // Load the Windows-cached names up front so live findings carry a name
        // even when the advertisement does not.
        _cachedNames = await CachedNamesAsync(ct);

        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _onAdvertised = targetName is null
            ? null
            : (name, flag) => { if (name == targetName && flag is not null) stop.TrySetResult(true); };

        bool watching = false;
        try
        {
            _watcher.Start();
            watching = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[WARN] BLE advertisement watcher unavailable ({ex.Message}); " +
                "falling back to known-device enumeration");
        }

        var wait = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), ct);
        await Task.WhenAny(wait, stop.Task);
        if (watching)
        {
            try { _watcher.Stop(); } catch { }
        }
        _onAdvertised = null;

        if (!watching)
        {
            try
            {
                var known = await DeviceInformation.FindAllAsync(
                    "BluetoothLEDevice?Name=*", null, DeviceInformationKind.AssociationEndpoint);
                foreach (var d in known)
                {
                    if (string.IsNullOrEmpty(d.Name))
                        continue;
                    _seen.TryAdd(
                        Interlocked.Increment(ref _fallbackKey),
                        new ScannedDevice(d.Name, d.Id, 0, null));
                }
            }
            catch
            {
                // best effort
            }
        }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        // Names of devices that advertised without one were already filled from
        // the Windows cache as they were found; the final list is the merged set.
        return _seen.Values
            .Select(d =>
                string.IsNullOrEmpty(d.Name) && _cachedNames.TryGetValue(d.Address, out var name)
                    ? d with { Name = name }
                    : d)
            .OrderByDescending(d => d.Rssi)
            .ToList();
    }

    /// <summary>
    /// Maps the normalized 12-hex-char address to the name Windows has cached for it
    /// (from the Bluetooth device database). Best-effort: empty map on failure.
    /// </summary>
    private static async Task<Dictionary<string, string>> CachedNamesAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // The store enumeration can hang (adapter busy right after a
            // connection attempt); bound it so the scan always starts.
            var task = DeviceInformation.FindAllAsync(
                null, null, DeviceInformationKind.AssociationEndpoint).AsTask();
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            if (finished != task)
                return map;
            var known = await task;
            foreach (var d in known)
            {
                if (string.IsNullOrEmpty(d.Name))
                    continue;
                var address = AddressFromDeviceId(d.Id);
                if (address is not null)
                    map[address] = d.Name;
            }
        }
        catch
        {
            // best effort
        }
        return map;
    }

    /// <summary>
    /// Extracts the device address (12 hex chars) from a <see cref="DeviceInformation.Id"/>
    /// such as <c>BluetoothLE#BluetoothLE00:1a:7d:da:71:15-38:8c:ef:4f:74:29</c>.
    /// </summary>
    private static string? AddressFromDeviceId(string id)
    {
        var lastDash = id.LastIndexOf('-');
        if (lastDash < 0)
            return null;
        var device = id[(lastDash + 1)..];
        var hex = new string(device.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 ? hex : null;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var advertisement = args.Advertisement;
        var name = advertisement?.LocalName;
        if (string.IsNullOrEmpty(name) && advertisement is not null)
            name = AdvParser.ParseNameFromSections(advertisement.DataSections);
        var address = args.BluetoothAddress;
        var rssi = args.RawSignalStrengthInDBm;
        var flag = advertisement is null ? null : ParseFlag(advertisement);
        if (string.IsNullOrEmpty(name) && _cachedNames.TryGetValue(address.ToString("X12"), out var cachedName))
            name = cachedName;
        name ??= string.Empty;

        bool isNew = !_seen.ContainsKey(address);
        _seen.AddOrUpdate(
            address,
            key => new ScannedDevice(name, key.ToString("X12"), rssi, flag),
            (_, old) => new ScannedDevice(
                string.IsNullOrEmpty(name) ? old.Name : name,
                old.Address,
                Math.Max(old.Rssi, rssi),
                flag is null ? old.Flag : flag));

        if (isNew)
            DeviceFound?.Invoke(_seen[address]);

        if (_onAdvertised is { } callback)
        {
            var merged = _seen[address];
            callback(merged.Name, merged.Flag);
        }
    }

    /// <summary>
    /// Reads the encryption flag from the manufacturer-specific (0xFF) AD
    /// sections of the advertisement: section data is
    /// <c>[companyLo, companyHi, flag, ...]</c>.
    /// </summary>
    private static byte? ParseFlag(BluetoothLEAdvertisement advertisement)
    {
        var sections = advertisement.GetSectionsByType(AdvParser.ManufacturerSpecific);
        if (sections is null)
            return null;
        foreach (var section in sections)
        {
            if (section.Data is null)
                continue;
            var data = section.Data.ToArray();
            var flag = AdvParser.ParseFlagFromManufacturerData(data);
            if (flag is not null)
                return flag;
        }
        return null;
    }

    /// <summary>Stops the advertisement watcher.</summary>
    public void Dispose()
    {
        try { _watcher.Stop(); } catch { }
    }
}
