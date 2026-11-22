# Indevolt CLI

A Windows C# client for the Indevolt (Solarman/IGene) battery's BLE
configuration API: scan/select a battery, connect with the device password,
assign a Wi-Fi SSID+password, plus raw AT command access.

## Requirements

- Windows 10/11
- A Bluetooth LE USB adapter
- .NET 8 SDK (or higher)

## Build & run

```powershell
dotnet build src\IndevoltCli
dotnet run --project src\IndevoltCli -- -- help
```

## Quick start

The battery must be in **AP mode**, advertising as `AP_<SN>` (SN = device
serial, a single leading `0` stripped if present). The factory device
password is `1111111111111111`.

```powershell
# 1. List nearby AP_<SN> batteries
dotnet run --project src\IndevoltCli -- list

# 2. Configure: connect with the device password, then assign Wi-Fi
dotnet run --project src\IndevoltCli -- config 123 --ssid MyWiFi --password mysecretpw --device-password 1111111111111111

# 3. Query the firmware version (AT+VER)
dotnet run --project src\IndevoltCli -- version 123

# 4. Read all device info the AT channel offers (version, type, WAN, APN…)
dotnet run --project src\IndevoltCli -- info 123

# 5. Send a raw AT command
dotnet run --project src\IndevoltCli -- send 123 --command "AT+DTYPE"
```

## Layout

- `docs/PROTOCOL.md` — full protocol specification (GATT profile, discovery,
  configuration sequence, command table, encryption)
- `docs/AT-Commands.md` — per-command AT reference
- `src/IndevoltCli` — the tool

## Disclaimer

Unofficial community tool for interoperability with your own
Indevolt/Solarman/IGene hardware. Not affiliated with the vendor; use at
your own risk.
