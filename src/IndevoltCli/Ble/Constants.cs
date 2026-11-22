namespace IndevoltCli.Ble;

/// <summary>
/// Fixed values from <c>docs/PROTOCOL.md</c>: GATT UUIDs (§1), product id,
/// factory password and the timeouts/marker schedule of §3–§4.
/// </summary>
public static class Constants
{
    /// <summary>GATT service; must be present for the "AT channel" to be supported.</summary>
    public static readonly Guid AtService = new("0000fee7-0000-1000-8000-00805f9b34fb");

    /// <summary>Command write characteristic (WriteWithResponse).</summary>
    public static readonly Guid CommandWrite = new("0000fec7-0000-1000-8000-00805f9b34fb");

    /// <summary>Response notification characteristic (replies arrive here).</summary>
    public static readonly Guid ResponseNotify = new("0000fec8-0000-1000-8000-00805f9b34fb");

    /// <summary>Status notification/indication characteristic (also watched).</summary>
    public static readonly Guid StatusNotify = new("0000fed4-0000-1000-8000-00805f9b34fb");

    /// <summary>Read characteristic (declared by the profile; unused by this CLI).</summary>
    public static readonly Guid ReadChar = new("0000fed6-0000-1000-8000-00805f9b34fb");

    /// <summary>Client characteristic configuration descriptor.</summary>
    public static readonly Guid Cccd = new("00002902-0000-1000-8000-00805f9b34fb");

    /// <summary>Product ID embedded in commands (e.g. <c>AT+IGDEVAUTH=214028,7,0</c>).</summary>
    public const string ProductId = "214028";

    /// <summary>
    /// Local Bluetooth-adapter prefix of THIS machine, as embedded in the
    /// <c>BluetoothLE#...-&lt;mac&gt;</c> device-store ids that
    /// <see cref="BluetoothLEDevice.FromIdAsync(string)"/> wants (learned
    /// from a store entry for the battery). Machine-specific: set the
    /// <c>INDEVOLT_STORE_PREFIX</c> environment variable on other machines.
    /// </summary>
    public static string StorePrefix =>
        Environment.GetEnvironmentVariable("INDEVOLT_STORE_PREFIX")
        ?? "BluetoothLE#BluetoothLE00:1a:7d:da:71:15";

    /// <summary>Factory device password (16 chars, PROTOCOL.md §1).</summary>
    public const string DefaultDevicePassword = "1111111111111111";

    /// <summary>MTU requested after connecting; the app tries it 3 times (PROTOCOL.md §1).</summary>
    public const uint RequestedMtu = 203;

    /// <summary>
    /// Per-command reply timeout for the query paths (version/info/send).
    /// The app uses 10 s (§4), but a freshly-woken device answers each
    /// command after ~13 s (verified live): with a 10 s budget every reply
    /// landed in the NEXT command's window and got misattributed. 25 s
    /// keeps a cold reply in its own slot.
    /// </summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(25);

    /// <summary>AT+IGDEVAUTH timeout (10 s, §4).</summary>
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);

    /// <summary>AT+IGTNETT timeout (10 s, §4).</summary>
    public static readonly TimeSpan NetTimeout = TimeSpan.FromSeconds(10);

    /// <summary>AT+CONFIG timeout (60 s, §4).</summary>
    public static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Default scan window for <c>list</c>.</summary>
    public static readonly int DefaultScanSeconds = 10;

    /// <summary>
    /// Default scan window for the connect verbs (config/version/info/send).
    /// The battery duty-cycles its BLE advertising in sparse bursts, so the
    /// window must be generous to catch one.
    /// </summary>
    public static readonly int DefaultConnectScanSeconds = 120;

    /// <summary>Budget for locating a device by address before connecting.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Budget for GATT characteristic discovery.</summary>
    public static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Budget for enabling notifications on a characteristic.</summary>
    public static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Budget for one WriteWithResponse on fec7.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The 100 ms delays between steps of the §3 sequence.</summary>
    public static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(100);
}
