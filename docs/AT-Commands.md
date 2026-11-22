# AT Command Reference — Indevolt (Solarman/IGene) Battery

Reference for the BLE AT-command interface of the Indevolt solar battery.
The single source of truth is [PROTOCOL.md](./PROTOCOL.md); where that document
marks something as uncertain or unverified, it is marked so here.

## How the commands work

The battery exposes a GATT "AT channel":

| Item | UUID |
|---|---|
| Service (must be present = AT channel supported) | `0000fee7-0000-1000-8000-00805f9b34fb` |
| Command write characteristic | `0000fec7-0000-1000-8000-00805f9b34fb` |
| Response notification characteristic | `0000fec8-0000-1000-8000-00805f9b34fb` |
| Status notification/indicate characteristic (also watched) | `0000fed4-0000-1000-8000-00805f9b34fb` |

Framing and transport:

- Commands are written to `fec7` as **UTF-8 text, `\r\n`-terminated**.
- Replies arrive as **notifications on `fec8`** (the `fed4` status
  characteristic is also watched).
- Transport is LE only; the client requests **MTU 203** after connecting
  (the app retries 3 times).
- The **product ID `214028`** is embedded in several commands
  (`AT+IGDEVAUTH`, `AT+IGTNETT`, `AT+RSTCOUNT`, `AT+yzapp`).
- Factory device password for a fresh unit: `1111111111111111` (16 chars).

### Response matching rules

- Replies are plain text, matched **case-insensitive**.
- Each send is matched against a **success-marker list** and a
  **failure-marker list**; the **first marker hit decides**; no marker hit by
  the per-command timeout = failure.
- `+ok` / `+OK` and `+err` / `+ERR` are the universal success/failure
  convention across the command set.
- Per-command timeout: **10 s** for all commands except `AT+CONFIG`, which
  uses **60 s**.

---

## AT+VER

Version / readiness query. The first command sent on the channel, and the
authentication probe on auth-required devices.

**Syntax**

```
AT+VER\r\n
```

**Parameters** — none.

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`

**Timeout**: 10 s.

**Notes**

- On auth-required devices (device flag `a1`) the result must be `1`;
  failure maps to result code `-3002` (see [Result codes](#result-codes)).
- Live reply (NUL-padded ciphertext, 128 bytes):

  ```
  +ok\r\n
  [11:27:54 May 29 2026] sdk:ECR6600F_v2.1.23.28_yingzhen wifi:V2.0.0\r\n
  [15:52:30 Aug 14 2026] igen adapter:V1.0.3<\0 padding>
  ```

---

## AT+IGDEVAUTH

Device authentication handshake, sent on password-mode devices after `AT+VER`.

**Syntax**

```
AT+IGDEVAUTH=214028,7,%d\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| Product ID | `%d` | Fixed: `214028` |
| Auth method | `%d` | Fixed: `7` |
| Session mode | `%d` | `0` = plaintext, `1` = encrypted session |

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`

**Timeout**: 10 s.

**Notes**

- The trailing `0`/`1` must reflect whether the session is encrypted:
  `1` when encryption is enabled (ADV flag `'1'` or `'2'`), `0` for plaintext
  (ADV flag `'0'`).
- Sent after a 100 ms delay on password-mode devices (`U0||W0`); the result
  must be true.

---

## AT+IGTNETT

Data policy: whether the battery must stay Wi-Fi only or may use mobile data.
This is an **optional** step driven by the app's `deviceDataMode` parameter;
it is not part of the core factory flow.

**Syntax**

```
AT+IGTNETT=214028,%d,%d\r\n
```

Two forms are built by the app:

| Form | Meaning |
|---|---|
| `AT+IGTNETT=214028,1,1\r\n` | Wi-Fi only (default) |
| `AT+IGTNETT=214028,0,0\r\n` | Allow mobile data |

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| Product ID | `%d` | Fixed: `214028` |
| Field 2 | `%d` | `1` in the Wi-Fi-only form, `0` in the mobile-data form; exact semantics not documented |
| Field 3 | `%d` | Same value as field 2 in both observed forms; exact semantics not documented |

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`

**Timeout**: 10 s.

**Notes**

- Only the two forms above were observed; other value combinations are
  unverified.

---

## AT+CONFIG

Assigns the Wi-Fi network the battery should join. The device tries to join
and reports success; this is the final step of the factory configuration flow.

**Syntax**

```
AT+CONFIG=<ssid>,<pwd>\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| ssid | `%s` | Target Wi-Fi SSID. Sanitized before sending: special characters stripped, leading character kept |
| pwd | `%s` | Wi-Fi password. Empty password → literal `none` |

**Response**

- Success marker: `+ok=connected` (exact marker, not the generic `+ok`)
- Failure markers: `+err`, `ssid error`, `pass error`

**Timeout**: **60 s** — no `+ok=connected` within 60 s → result code `-1020`.

**Notes**

- The result must be `1`; on success the final reported result code is
  `1000` (see [Result codes](#result-codes)).
- Sent after a 100 ms delay following the previous step.
- The AT layer only ever sees the SSID+password string; the device probes the
  network itself. The app's Wi-Fi security mapping (`WPA2PSK`, `WPAPSK`,
  `OPEN`, TKIP/AES/WEP subtypes) is never transmitted on the AT channel.

---

## AT+WMODE

Set Wi-Fi mode.

**Syntax**

```
AT+WMODE=%s\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| mode | `%s` | Wi-Fi mode value; allowed values are **not documented** |

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`
- Markers are the universal convention; not verified by an exercised send in
  the app (uncertain).

**Timeout**: 10 s.

---

## AT+WSKEY

Set Wi-Fi security key (type, key).

**Syntax**

```
AT+WSKEY=%s,%s\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| type | `%s` | Wi-Fi security type; accepted values are **not documented** (the app's `WPA2PSK`/`WPAPSK`/`OPEN` mapping does not reach the AT layer) |
| key | `%s` | Wi-Fi security key / password |

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`
- Universal convention; not verified by an exercised send in the app (uncertain).

**Timeout**: 10 s.

---

## AT+WSSSID

Set Wi-Fi SSID.

**Syntax**

```
AT+WSSSID=%s\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| ssid | `%s` | Wi-Fi SSID (no sanitization documented for this command itself) |

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`
- Universal convention; not verified by an exercised send in the app (uncertain).

**Timeout**: 10 s.

---

## AT+IGCPW

Change the device password.

**Syntax**

```
AT+IGCPW=%s,%s,%s\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| Param 1 | `%s` | Presumed old device password |
| Param 2 | `%s` | Presumed new device password |
| Param 3 | `%s` | Presumed new device password (repeat); **exact purpose is uncertain** |

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`
- Universal convention; not verified by an exercised send in the app (uncertain).

**Timeout**: 10 s.

---

## AT+YWFPW

Set/change Wi-Fi password.

**Syntax**

```
AT+YWFPW=%s,%s,%s\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| Param 1 | `%s` | Not documented beyond "Wi-Fi password" purpose (uncertain) |
| Param 2 | `%s` | Not documented (uncertain) |
| Param 3 | `%s` | Not documented (uncertain) |

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`
- Universal convention; not verified by an exercised send in the app (uncertain).

**Timeout**: 10 s.

---

## AT+INVDATA

Invalidate / clear stored data.

**Syntax**

```
AT+INVDATA=\r\n
```

**Parameters** — none (empty argument after `=`, as built in the app).

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`
- Universal convention; not verified by an exercised send in the app (uncertain).

**Timeout**: 10 s.

---

## AT+RSTCOUNT

Reset counter.

**Syntax**

```
AT+RSTCOUNT=214028,0\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| Product ID | `%d` | Fixed: `214028` |
| Counter | `%d` | Fixed: `0` (only value observed in the app) |

**Response**

- Success markers: `+ok`, `+OK`
- Failure markers: `+err`, `+ERR`
- Universal convention; not verified by an exercised send in the app (uncertain).

**Timeout**: 10 s.

---

## AT+DTYPE

Query device type.

**Syntax**

```
AT+DTYPE\r\n
```

**Parameters** — none.

**Response**

- No success/failure markers are documented for this command in the app;
  `+ok`/`+OK` vs `+err`/`+ERR` (universal convention) is assumed (uncertain).

**Timeout**: 10 s.

**Notes**

- Live reply: `+ok=18504,18504` — type ID, repeated (device type `18504`).

---

## AT+WANN

WAN network info (mode / IP / subnet / gateway).

**Syntax**

```
AT+WANN\r\n
```

**Parameters** — none.

**Response**

- No success/failure markers are documented for this command in the app;
  `+ok`/`+OK` vs `+err`/`+ERR` (universal convention) is assumed (uncertain).

**Timeout**: 10 s.

**Notes**

- Live reply: `+ok=DHCP,10.0.3.111,255.255.255.0,10.0.3.1` —
  address mode, IP, netmask, gateway. Confirms the device was already
  provisioned (Wi-Fi).

---

## AT+YZAPN

Set cellular APN.

**Syntax**

```
AT+YZAPN\r\n
```

**Parameters** — none documented (uncertain whether
arguments exist).

**Response**

- No success/failure markers are documented for this command in the app;
  `+ok`/`+OK` vs `+err`/`+ERR` (universal convention) is assumed (uncertain).

**Timeout**: 10 s.

---

## AT+YZNETCHECK

Cellular network check.

**Syntax**

```
AT+YZNETCHECK\r\n
```

**Parameters** — none.

**Response**

- No success/failure markers are documented for this command in the app;
  `+ok`/`+OK` vs `+err`/`+ERR` (universal convention) is assumed (uncertain).

**Timeout**: 10 s.

**Notes**

- Live reply on a Wi-Fi-provisioned unit: `+ok=psd error` — a `+ok`
  prefix with an error detail (no cellular path). Not an AT error.

---

## AT+yzapp

Read app/product config.

**Syntax**

```
AT+yzapp=214028,read\r\n
```

**Parameters**

| Name | Format | Constraints |
|---|---|---|
| Product ID | `%d` | Fixed: `214028` |
| Operation | `%s` | Fixed: `read` (only value observed in the app) |

**Response**

- No success/failure markers are documented for this command in the app;
  `+ok`/`+OK` vs `+err`/`+ERR` (universal convention) is assumed (uncertain).

**Timeout**: 10 s.

**Notes**

- Live reply on this unit: `+ok=psd error` — the same "error detail" shape
  as `AT+YZNETCHECK`, presumably "no stored app config to report".

---

## Result codes

Result codes reported by the configuration flow to the UI layer (from
PROTOCOL.md §4):

| Code | Meaning |
|---|---|
| `1000` | Wi-Fi config success (`AT+CONFIG` completed, `+ok=connected`) |
| `1002` | Device already configured / in use |
| `1003` | Already fully configured (nothing to do) |
| `1004` | Success via alternate (AP) sub-flow |
| `-1` | Timed out (mapped to `-1020` for `AT+CONFIG`) |
| `-1001` | GATT connection lost / connect failed |
| `-1002` | Phone Wi-Fi state precondition failed (Android-only) |
| `-1003` | Device not found in scan |
| `-1004` | Pre-check failure (member of the connect/MTU/location/state pre-check group; exact trigger not determined) |
| `-1006` | MTU negotiation failed (pre-check group) |
| `-1013` | Pre-check failure (member of the connect/MTU/location/state pre-check group; exact trigger not determined) |
| `-1014` | MTU negotiation failed (pre-check group) |
| `-1020` | `AT+CONFIG` timeout (60 s without `+ok=connected`) |
| `-3000` | Device password empty (auth-required device) |
| `-3001` | Device password invalid format (auth-required device) |
| `-3002` | `AT+VER` auth-probe failed |
| `-3003` | `AT+VER` auth-probe failed (paired with `-3002`; exact distinction not determined) |

---

## Session sequence

Factory configuration of a new device, as run by the app:

1. **Scan** for `AP_<SN>`; parse the ADV flag (encryption mode).
   100 ms delay.
2. **State checks**: abort if already configuring; result code `1002` =
   device already in use; result code `1003` = already fully configured.
3. **Connect** GATT (LE), up to 3 attempts (failure → `-1001`).
4. **Request MTU 203** (3 attempts; failure → `-1006` / `-1014`).
5. **Verify the `0000fee7` service** exists ("AT channel supported").
6. **Enable AT channel**: subscribe to notifications on `fec8` (plus `fed4`;
   failure → `-1001`).
7. **`AT+VER\r\n`** after a 100 ms delay — mandatory on auth-required
   devices, where the device password must be non-empty (else `-3000`) and
   8 characters from the SN charset (else `-3001`); result must be `1`
   (failure → `-3002`).
8. **`AT+IGDEVAUTH=214028,7,<0|1>\r\n`** on password-mode devices
   (`U0||W0`), after a 100 ms delay; result must be true.
9. **`AT+IGTNETT=214028,1,1`** (Wi-Fi only) or
   **`AT+IGTNETT=214028,0,0`** (allow mobile data) — optional step driven
   by the `deviceDataMode` parameter, not part of the core flow.
10. **`AT+CONFIG=<ssid>,<pwd>\r\n`** after a 100 ms delay (timeout →
    `-1020`); result must be `1` → final code **`1000`** = success.

---

## Wire format & encryption

### ADV flag byte

The device advertises with name **`AP_<SN>`** (SN with a single leading `0`
stripped if present). Its ADV data contains a manufacturer-specific
structure (AD type `0xFF`):

```
[len, 0xFF, companyLo, companyHi, flag, ...]
```

`flag` is the first payload byte after the 2-byte company ID, ASCII:

| Flag | Meaning |
|---|---|
| `'0'` (48) | Plaintext, no encryption |
| `'1'` (49) | Encryption supported; key = MD5-derived from `"214028"+password` (derivation below) |
| `'2'` (50) | Encryption; key = hex-of-chars of the **device password** (derivation below) |

The remaining payload bytes are 16/32-bit UUIDs relative to base
`0000e600-0000-1000-8000-00805f9b34fb` (advertised service UUIDs).

When the flag indicates encryption, every payload is AES-CBC with
**key == IV** — a 16-character ASCII hex string derived from the password:

- **Outgoing commands** are PKCS5-padded (app encrypts with
  `AES/CBC/PKCS5PADDING`).
- **Incoming replies are NOT PKCS7-padded** — the device NUL-pads the
  plaintext to a 16-byte multiple, and the client decrypts with
  `AES/CBC/NoPadding`, ignoring the NUL tail. A PKCS7 unpad of a device
  reply throws; decrypt raw and cut trailing NULs.

(Verified live on a flag-`'2'` unit with an 8-digit device password.)

### Key derivation — flag `'2'` (device-password key)

```
key = hex(charCode(c) for c in password)
```

truncated to 16 hex characters.

### Key derivation — flag `'1'` (MD5, 4×4-XOR)

1. `s = "214028" + password` (password truncated to 15 chars)
2. hex of `s` → byte-wise `0xFF - b` → bytes
3. `md5(bytes)` → byte-wise `0xFF - b` again (as hex pairs)
4. reshape into a 4×4 matrix of hex-pair values, XOR each column, hex the 4 results
5. lowercase, pad/truncate to 16 chars → key/IV.

Plaintext devices (flag `'0'` — typical for a fresh factory unit) need no crypto.
