# Indevolt (Solarman/IGene) Battery — BLE AT Command Protocol

Protocol reference for the battery's BLE AT-command interface as used by the
Indevolt app (a rebranded Solarman/IGene app).

## 1. GATT profile

| Item | UUID |
|---|---|
| Service (must be present = "AT channel" supported) | `0000fee7-0000-1000-8000-00805f9b34fb` |
| Command write characteristic | `0000fec7-0000-1000-8000-00805f9b34fb` |
| Response notification characteristic | `0000fec8-0000-1000-8000-00805f9b34fb` |
| Status notification/indicate characteristic | `0000fed4-0000-1000-8000-00805f9b34fb` |
| Read characteristic | `0000fed6-0000-1000-8000-00805f9b34fb` |
| CCCD | `00002902-0000-1000-8000-00805f9b34fb` |

- Commands are written to `fec7` as UTF-8 text, `\r\n`-terminated.
- Replies arrive as notifications on `fec8` (also watch `fed4`).
- Transport: LE only (`connectGatt(ctx, false, cb, TRANSPORT_LE)`).
- Request **MTU 203** after connecting (3 retries in the app).
- Product ID embedded in commands: **`214028`**.
- Factory device password: **`1111111111111111`** (16 chars).

## 2. Discovery

- Device advertises with name **`AP_<SN>`** where SN is the device serial,
  with a single leading `0` stripped if present (`"0123"` → `AP_123`, `"123"` → `AP_123`).
- ADV data contains a **manufacturer-specific structure** (AD type `0xFF`):
  `[len, 0xFF, companyLo, companyHi, flag, ...]`
  - `flag` = first payload byte after the 2-byte company ID (ASCII):
    - `'0'` (48): plaintext, no encryption
    - `'1'` (49): encryption **supported**; key = MD5-derived from `"214028"+password` (see §5)
    - `'2'` (50): encryption, key = hex-of-chars of the **device password** (see §5)
  - Remaining payload bytes are 16/32-bit UUIDs relative to base
    `0000e600-0000-1000-8000-00805f9b34fb` (advertised service UUIDs).
- The app matches devices by exact advertised name `AP_<SN>`, reads the flag
  from ADV before connecting, then starts the GATT connection.

## 3. Configuration sequence (factory/new device)

The app's flow for factory-configuring a new device:

1. Scan for `AP_<SN>`; parse ADV flag (encryption mode).
2. 100 ms delay.
3. State checks:
   - already configuring → abort
   - result code **1002** = device already in use/configured
   - result code **1003** = already fully configured (no-op success)
4. **Connect** GATT (LE), up to 3 attempts. Failure → code -1001.
5. **Request MTU 203** (3 attempts). Failure → -1006 / -1014.
6. Verify the `0000fee7` service exists ("AT channel supported").
7. **Enable AT channel**: subscribe to notifications (`fec8`, plus `fed4`).
   Failure → -1001.
8. If the device is in auth-required mode (`a1`):
   - devicePassword must be non-empty (else code **-3000**) and 8 chars from
     the SN charset `0123456789abcdefghigklmnopqrstuvwxyzABC...XYZ` (else **-3001**).
   - 100 ms delay, then send **`AT+VER\r\n`** (code path -3002); result must be 1.
9. If password mode (`U0||W0`): 100 ms delay, send **`AT+IGDEVAUTH=214028,7,<0|1>\r\n`**;
   the trailing 0/1 = encryption enabled or not; result must be true.
10. Default path (new device, Wi-Fi assignment): 100 ms delay, then
    **`AT+CONFIG=<ssid>,<pwd>\r\n`** (code path -1020 on timeout);
    result must be 1 → final code **1000** = success.

Optional data-mode step (driven by the JS `deviceDataMode` param, not in the
core flow): `AT+IGTNETT=214028,1,1\r\n` = Wi-Fi only (default),
`AT+IGTNETT=214028,0,0\r\n` = allow mobile data.

## 4. AT commands

All commands end with `\r\n`. Replies are plain text, matched **case-insensitive**.
Each send is matched against a success-marker list and a failure-marker list;
first marker hit decides; no hit by the timeout = failure.

| Command | Timeout | Success markers | Failure markers | Purpose |
|---|---|---|---|---|
| `AT+VER\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Version/readiness; first command on the channel (also the auth probe in auth-required mode) |
| `AT+IGDEVAUTH=214028,7,%d\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Device authentication handshake; `%d` = 1 when the session is encrypted, 0 plaintext. 214028 = product ID, 7 = auth method |
| `AT+IGTNETT=214028,1,1\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Data policy: Wi-Fi only |
| `AT+IGTNETT=214028,0,0\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Data policy: allow mobile data |
| `AT+CONFIG=<ssid>,<pwd>\r\n` | **60 s** | `+ok=connected` | `+err`, `ssid error`, `pass error` | Assign Wi-Fi network; device tries to join and reports connected. Empty password → literal `none`. SSID is sanitized (special chars stripped, leading char kept) before sending |
| `AT+WMODE=%s\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Set Wi-Fi mode |
| `AT+WSKEY=%s,%s\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Set Wi-Fi security key (type, key) |
| `AT+WSSSID=%s\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Set Wi-Fi SSID |
| `AT+IGCPW=%s,%s,%s\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Change device password (old, new, new?) |
| `AT+YWFPW=%s,%s,%s\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Set/change Wi-Fi password |
| `AT+INVDATA=` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Invalidate/clear stored data |
| `AT+RSTCOUNT=214028,0\r\n` | 10 s | `+ok`, `+OK` | `+err`, `+ERR` | Reset counter |
| `AT+DTYPE\r\n` | 10 s | — | — | Query device type |
| `AT+WANN\r\n` | 10 s | — | — | (WAN name / APN related) |
| `AT+YZAPN\r\n` | 10 s | — | — | Set cellular APN |
| `AT+YZNETCHECK\r\n` | 10 s | — | — | Cellular network check |
| `AT+yzapp=214028,read\r\n` | 10 s | — | — | Read app/product config |

Success/failure markers for the last eight commands are not all exercised;
`+ok`/`+OK` vs `+err`/`+ERR` is the universal convention.

### Result codes reported to the UI layer

| Code | Meaning |
|---|---|
| 1000 | Wi-Fi config success (AT+CONFIG completed, `+ok=connected`) |
| 1002 | Device already configured/in use |
| 1003 | Already fully configured (nothing to do) |
| 1004 | Success via alternate (AP) sub-flow |
| -1 | Timed out (mapped to -1020 for CONFIG) |
| -1001 | GATT connection lost / connect failed |
| -1002 | Phone Wi-Fi state precondition failed (Android-only) |
| -1003 | Device not found in scan |
| -1004 / -1006 / -1013 / -1014 | Pre-check failures (connect/MTU/location/state) |
| -1020 | AT+CONFIG timeout (60 s without `+ok=connected`) |
| -3000 | Device password empty (auth-required device) |
| -3001 | Device password invalid format (auth-required device) |
| -3002 / -3003 | AT+VER auth-probe failed |

## 5. Encryption (only when ADV flag is `'1'` or `'2'`)

When the ADV flag says encryption, every command payload and every reply is
AES-**CBC** with **key == IV** = a 16-character ASCII hex string derived
from the password. The two directions use different
padding — this tripped up the first live test:

- **Outgoing commands** (encrypted by the client):
  `AES/CBC/PKCS5PADDING`, key == IV.
- **Incoming replies** (decrypted by the client):
  `AES/CBC/NoPadding` — the device NUL-pads the plaintext to a 16-byte
  multiple instead of PKCS7, so PKCS7 unpadding throws. Decrypt raw and cut
  the trailing NULs.

Verified live (flag `'2'`, 8-digit device password), `AT+VER` reply
decrypted to:

```
+ok\r\n[11:27:54 May 29 2026] sdk:ECR6600F_v2.1.23.28_yingzhen wifi:V2.0.0\r\n[15:52:30 Aug 14 2026] igen adapter:V1.0.3<\x00 padding>
```

Key derivation:

- Flag `'2'` (device-password key):
  `key = hex(charCode(c) for c in password)` truncated to 16 hex chars.
- Flag `'1'` (MD5 derivation):
  1. `s = "214028" + password` (password truncated to 15 chars)
  2. hex of s → byte-wise `0xFF - b` → bytes
  3. `md5(bytes)` → byte-wise `0xFF - b` again (as hex pairs)
  4. reshape into 4×4 matrix (row-major) of the 16 byte values, XOR each
     column → 4 bytes → 8-char lowercase hex string `H`
  5. **key/IV = hex of the ASCII char codes of `H`** (each of the 8 chars →
     its 2-digit ASCII hex code, e.g. `a3f2b1c9` → `6133663262316339`) —
     identical in shape to the flag-`'2'` transform; the result is exactly
     16 chars.

Plaintext devices (flag `'0'` — typical for a fresh factory unit) need no crypto.

## 6. Notes / gotchas

- **Advertising is bursty.** The battery duty-cycles its ADV (bursts every
  few minutes, each short). Scanning must be **active** (Android
  `SCAN_MODE_LOW_LATENCY`, WinRT `ScanningMode.Active`) — the device answers
  probes; passive scans miss most bursts. Give the scan window ≥ 2 min.
- **The battery only keeps a connection during a burst.** When the burst ends
  it drops the client mid-discovery (status → Disconnected) — but it will
  still *accept* a reconnect right after, only to drop it again (verified
  live: 3 consecutive connect-drops in a row). Retrying the connect alone
  is therefore futile; re-SCAN to resync with the next burst. The CLI does
  up to 3 scan → connect rounds (round 1 with the full scan window, retries
  with a shorter one) before giving up. Send the first AT command as soon
  as the channel opens; a write after the burst ends gets no reply — a
  timeout, not an error.
- The device may push unrelated encrypted status blobs (different key or
  content); the client must skip any payload that doesn't decrypt to text
  under its own key (the app's handler does exactly this).
- **Reply latency is variable.** A warm device answers in well under 10 s;
  a freshly-woken one took ~13 s per command (verified live). With a 10 s
  per-command timeout every reply lands in the *next* command's window and
  gets misattributed (observed: `AT+DTYPE` printed the `AT+VER` firmware
  string). Use ≥ 20 s per command, or accept misattribution on a cold
  device.
- `AT+YZNETCHECK` on a Wi-Fi-provisioned unit answers `+ok=psd error`
  (no cellular path); it is not an AT error.
- **Windows/WinRT quirks** (from the live test, net8.0-windows `UseWinRT`):
  - `BluetoothLEDevice.FromIdAsync` needs the full device-store ID
    (`BluetoothLE#BluetoothLE<local-mac>-<remote-mac>`); a bare MAC is
    rejected. The store entry for an unpaired device comes and goes with the
    bursts, and the constructed ID from the local-adapter prefix resolves
    without a store entry (prefix = machine-specific; the local adapter MAC
    of this machine is `00:1a:7d:da:71:15`).
  - `DeviceInformation.FindAllAsync` (store enumeration) can hang for 15 s+
    right after a scan — always bound it.
  - `GattServices` is empty right after connecting (async discovery) — poll
    `GetGattServicesAsync()` until the AT service appears; characteristic
    discovery has the same race.
  - `DeviceWatcher` is not constructible in this SDK projection (no public
    ctors), so store entries can't be watched live; snapshot + constructed
    ID is the workable path.
- The app's configuration entry point takes `deviceSn`, `devicePassword`,
  `ssid`, `password`, `deviceDataMode`; progress events are emitted along the
  sequence above.
- A second flow lets the phone join the battery's own AP and push credentials
  over it — not covered here; the BLE AT flow is the default.
- A different product in the same app (a Wi-Fi logger / Modbus gateway) has
  its own commands — do not confuse them with the battery protocol.
- The phone's own Wi-Fi state is pre-checked on Android (it must be able to
  report the target SSID's security type: OPEN/WPA/WPA2/WEP). On Windows this
  check is irrelevant — we just send the credentials we were given.
- Wi-Fi security mapping used by the app: `WPA2PSK`, `WPAPSK`, `OPEN`,
  with TKIP/AES/WEP subtypes — the AT layer only ever sees the SSID+password
  string; the device probes the network itself.
