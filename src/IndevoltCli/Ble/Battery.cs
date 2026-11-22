using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using WinRT;

namespace IndevoltCli.Ble;

/// <summary>
/// A connected Indevolt battery: the 0000fee7 AT-channel service, fec7 (write)
/// / fec8 + fed4 (notifications) wired up, plus a small ring buffer of the
/// recent notification payloads (raw bytes + plain UTF-8 view).
///
/// Note on MTU: PROTOCOL.md §1 asks for MTU 203 after connecting. This WinRT
/// projection exposes no MTU-request API; the Windows GATT stack negotiates the
/// LE MTU automatically (typically 244+ on Windows 10/11), which covers the
/// 203 the protocol wants.
/// </summary>
public sealed class Battery(BluetoothLEDevice device) : IDisposable
{
    private const int MaxBufferedNotifications = 64;

    private GattDeviceService? _service;
    private GattCharacteristic? _write;
    private GattCharacteristic? _response;
    private GattCharacteristic? _status;
    private readonly object _gate = new();
    private readonly List<(long Seq, byte[] Bytes, string Plain)> _buffer = new();
    private long _seq;
    private volatile bool _connected = true;

    /// <summary>The connected device's canonical (colonless) Bluetooth address.</summary>
    public string Address => device.BluetoothAddress.ToString("X12");

    /// <summary>True while the GATT connection is up.</summary>
    public bool IsConnected => _connected;

    /// <summary>Raised once when the device goes away.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Raised for every notification payload (sequence number, raw bytes).</summary>
    public event Action<long, byte[]>? NotificationReceived;

    /// <summary>
    /// Connects to a battery by its Bluetooth address and opens the AT channel:
    /// verifies 0000fee7, then enables notifications on fec8 (and fed4,
    /// best-effort).
    /// </summary>
    public static async Task<Battery> ConnectAsync(string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Empty device address.", nameof(address));
        ct.ThrowIfCancellationRequested();

        // FromIdAsync wants the full device-store ID (BluetoothLE#...). We
        // don't know in advance which of the candidate forms this Windows
        // machine accepts in the battery's current state, so each attempt
        // tries them in order (they fail fast when rejected).
        var ids = CandidateIds(address);

        // The battery only keeps connections during its short advertising
        // bursts and drops the client when the burst ends. The app's flow
        // retries the connection up to 3 times — do the same.
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                Console.WriteLine($"[..] retrying connection ({attempt}/3) ...");
                await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
            }
            try
            {
                return await TryOpenAsync(ids, address);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last!;
    }

    private static async Task<Battery> TryOpenAsync(string[] ids, string address)
    {
        BluetoothLEDevice? found = null;
        for (var i = 0; i < ids.Length; i++)
        {
            try
            {
                found = await WithTimeout(
                    () => BluetoothLEDevice.FromIdAsync(ids[i]).AsTask(),
                    Constants.ConnectTimeout, "locating device");
                break;
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Timed out locating Bluetooth device {address}; is it advertising?");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"Access denied for Bluetooth device {address}. On Windows this usually means Bluetooth " +
                    $"is off or the app lacks Bluetooth/location permission.", ex);
            }
            catch (ArgumentException)
            {
                // this id form was rejected — try the next candidate
            }
        }
        if (found is null)
            throw new InvalidOperationException(
                $"Bluetooth device {address} not found (tried ids: {string.Join("; ", ids)}).");

        var battery = new Battery(found);
        try
        {
            await battery.OpenAsync();
            return battery;
        }
        catch
        {
            battery.Dispose();
            throw;
        }
    }

    private async Task OpenAsync()
    {
        var service = await FindAtServiceAsync();
        _service = service;

        // Characteristic discovery is async too: right after the service
        // appears the list can still be empty. Poll until fec7 + fec8 are
        // both present (or the budget runs out / the device drops us).
        var deadline = Environment.TickCount64 + (long)(Constants.ServiceTimeout.TotalMilliseconds * 2);
        while (true)
        {
            var chars = (await WithTimeout(
                () => service.GetCharacteristicsAsync().AsTask(),
                Constants.ServiceTimeout, "characteristic discovery"))
                .Characteristics.ToList();

            _write = chars.FirstOrDefault(c => c.Uuid == Constants.CommandWrite);
            _response = chars.FirstOrDefault(c => c.Uuid == Constants.ResponseNotify);
            _status = chars.FirstOrDefault(c => c.Uuid == Constants.StatusNotify);
            if (_write is not null && _response is not null)
                break;

            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                throw new InvalidOperationException("Device disconnected during characteristic discovery.");
            if (Environment.TickCount64 >= deadline)
                throw new InvalidOperationException(
                    "Characteristics 0000fec7/0000fec8 not found on the AT service. Discovered: " +
                    (chars.Count > 0 ? string.Join(", ", chars.Select(c => c.Uuid)) : "(none)"));
            await Task.Delay(500);
        }

        device.ConnectionStatusChanged += OnConnectionStatusChanged;
        _response!.ValueChanged += OnValueChanged;
        if (_status is not null)
            _status.ValueChanged += OnValueChanged;

        await WithTimeout(
            () => _response.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(),
            Constants.NotifyTimeout, "enabling fec8 notifications");
        if (_status is not null)
        {
            try
            {
                await WithTimeout(
                    () => _status.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(),
                    Constants.NotifyTimeout, "enabling fed4 notifications");
            }
            catch (Exception)
            {
                // fed4 is auxiliary status traffic; fec8 is the reply channel and is mandatory.
            }
        }
    }

    /// <summary>
    /// GATT discovery is asynchronous on Windows: right after connecting,
    /// <see cref="BluetoothLEDevice.GattServices"/> can still be empty (the
    /// Android app waits for onServicesDiscovered the same way). Polls until
    /// the 0000fee7 AT service appears or the service budget runs out.
    /// </summary>
    private async Task<GattDeviceService> FindAtServiceAsync()
    {
        var deadline = Environment.TickCount64 + (long)(Constants.ServiceTimeout.TotalMilliseconds * 2);
        while (true)
        {
            GattDeviceService? service = null;
            var services = new List<GattDeviceService>();
            try
            {
                var result = await WithTimeout(
                    () => device.GetGattServicesAsync().AsTask(),
                    Constants.ServiceTimeout, "service discovery");
                services = result.Services.ToList();
                service = services.FirstOrDefault(s => s.Uuid == Constants.AtService);
            }
            catch (TimeoutException)
            {
                // discovery hung — retry once, then the deadline kicks in
            }
            catch (Exception)
            {
                // the device is going away; the status check below reports it
            }

            if (service is not null)
                return service;

            var status = device.ConnectionStatus;
            if (status != BluetoothConnectionStatus.Connected)
                throw new InvalidOperationException(
                    $"Device is not connected during service discovery (status: {status}).");

            if (Environment.TickCount64 >= deadline)
                throw new InvalidOperationException(
                    "Service 0000fee7 (AT channel) not found on this device. " +
                    $"Discovered services: {(services.Count > 0 ? string.Join(", ", services.Select(s => s.Uuid)) : "(none)")}");

            await Task.Delay(500);
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToArray();
        var seq = Interlocked.Increment(ref _seq);
        var plain = Encoding.UTF8.GetString(bytes);
        lock (_gate)
        {
            _buffer.Add((seq, bytes, plain));
            while (_buffer.Count > MaxBufferedNotifications)
                _buffer.RemoveAt(0);
        }
        NotificationReceived?.Invoke(seq, bytes);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool connected;
        try
        {
            connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch
        {
            connected = false;
        }
        if (connected)
        {
            _connected = true;
        }
        else
        {
            _connected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sequence number of the most recent notification (0 = none yet).</summary>
    public long MaxSeq => Interlocked.Read(ref _seq);

    /// <summary>A snapshot of the recent notification payloads, oldest first.</summary>
    public IReadOnlyList<(long Seq, byte[] Bytes, string Plain)> Recent()
    {
        lock (_gate)
            return _buffer.ToList();
    }

    /// <summary>Writes raw command bytes to fec7 (write with response, 10 s budget).</summary>
    public Task WriteAsync(byte[] data)
    {
        if (_write is null)
            throw new InvalidOperationException("AT channel not open.");
        if (!_connected)
            throw new InvalidOperationException("Device is disconnected.");
        return WithTimeout(async () =>
        {
            var status = await _write.WriteValueAsync(ToBuffer(data)).AsTask();
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"GATT write failed: {status}.");
        }, Constants.WriteTimeout, "GATT write");
    }

    /// <summary>Detaches the event handlers and closes the GATT device.</summary>
    public void Dispose()
    {
        if (_response is not null)
            _response.ValueChanged -= OnValueChanged;
        if (_status is not null)
            _status.ValueChanged -= OnValueChanged;
        device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _connected = false;
        try
        {
            device.Dispose();
        }
        catch
        {
            // already gone
        }
    }

    private static IBuffer ToBuffer(byte[] data)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(data);
        return writer.DetachBuffer();
    }

    private static async Task<T> WithTimeout<T>(Func<Task<T>> work, TimeSpan timeout, string what)
    {
        var task = work();
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        if (done != task)
            throw new TimeoutException($"{what} timed out after {(int)timeout.TotalSeconds}s.");
        return await task;
    }

    private static async Task WithTimeout(Func<Task> work, TimeSpan timeout, string what)
    {
        var task = work();
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        if (done != task)
            throw new TimeoutException($"{what} timed out after {(int)timeout.TotalSeconds}s.");
        await task;
    }

    private static readonly object _storeGate = new();
    private static Dictionary<string, string>? _storeIds;
    private static string? _adapterPrefix;

    /// <summary>
    /// Snapshots the Bluetooth device store (15 s bound), merging the result
    /// into the cache used by <see cref="ResolveDeviceId(string)"/>.
    /// </summary>
    public static async Task PrepareDeviceStoreAsync(CancellationToken ct)
    {
        var byAddr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? prefix = null;
        try
        {
            var task = DeviceInformation.FindAllAsync(
                null, null, DeviceInformationKind.AssociationEndpoint).AsTask();
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            if (finished == task)
            {
                foreach (var d in await task)
                    RecordStoreEntry(d.Id, byAddr, ref prefix);
            }
        }
        catch
        {
            // best effort
        }
        lock (_storeGate)
        {
            _storeIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in byAddr)
                _storeIds[kv.Key] = kv.Value;
            _adapterPrefix ??= prefix;
        }
    }

    /// <summary>Records a <c>BluetoothLE#...-&lt;mac&gt;</c> store ID by address.</summary>
    private static void RecordStoreEntry(string id, Dictionary<string, string> byAddr, ref string? prefix)
    {
        if (!id.StartsWith("BluetoothLE#", StringComparison.Ordinal))
            return;
        var dash = id.LastIndexOf('-');
        if (dash < 0)
            return;
        prefix ??= id[..dash];
        var addr = NormalizeAddress(id[(dash + 1)..]);
        if (addr.Length == 12)
            byAddr[addr] = id;
    }

    /// <summary>


    /// <summary>
    /// Candidate device IDs for <see cref="BluetoothLEDevice.FromIdAsync(string)"/>,
    /// best first: exact store entry (if cached), then the constructed
    /// <c>BluetoothLE#...-&lt;mac&gt;</c> from the local adapter prefix
    /// (dynamic prefix if the store supplied one, otherwise this machine's
    /// known prefix), and finally the bare MAC as a last resort.
    /// </summary>
    private static string[] CandidateIds(string address)
    {
        var list = new List<string>();
        lock (_storeGate)
        {
            var target = NormalizeAddress(address);
            if (_storeIds is not null && _storeIds.TryGetValue(target, out var id))
                list.Add(id);
            var prefix = _adapterPrefix ?? Constants.StorePrefix;
            var constructed = prefix + "-" + ToAddressId(address);
            if (!list.Contains(constructed))
                list.Add(constructed);
        }
        var mac = ToAddressId(address);
        if (!list.Contains(mac))
            list.Add(mac);
        return list.ToArray();
    }

    /// <summary>Strips separators and upper-cases an address for comparison.</summary>
    private static string NormalizeAddress(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>
    /// Normalizes a device address to the colon-separated lowercase MAC
    /// <c>D427875ACC82</c> → <c>d4:27:87:5a:cc:82</c>; passes through anything
    /// that isn't 12 hex chars (already a full device ID).
    /// </summary>
    private static string ToAddressId(string address)
    {
        var hex = new string(address.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12)
            return address;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
