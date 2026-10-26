🇧🇷 Versão em português: [README.md](README.md)

# Brymen DMM REST

<p align="center"><img src="docs/images/minipa-et-2615a-bc-86x.webp" width="360"
  alt="Minipa ET-2615A (Brymen BM525s) with the BC-86X optical USB cable"></p>

.NET 10 port of *Bs82-52x Data Recording System V6.0.0.9*, Brymen's Windows software that reads the
multimeters of the BM52x and BM82x families through the **BC-86X** optical USB cable (**BU-86X** kit). Two
meters have been validated on the hardware:

- **Brymen BM525s**, sold in Brazil as the **Minipa ET-2615A**: BM52x family, with a datalogger;
- **Minipa ET-2517A**: BM82x family, without a datalogger.

The family is detected automatically on the 1st reading ([Supported meters](#supported-meters)).

The result is a service with a REST API that runs on **Linux arm64** (Raspberry Pi), **Linux x64** and,
for development, **Windows**. It provides:

- real-time readings as JSON, as a Server-Sent Events stream or over SignalR (each new reading from the
  multimeter is pushed to the connected clients);
- recording equivalent to the Graphical Recorder and the Data List, with CSV export in the original's format;
- Hi/Lo comparator;
- import of the datalogger memory (BM52x);
- minimal web dashboard.

To use the meter from another .NET program (8 or newer) without the API, use the
[`Brymen.Dmm`](src/Brymen.Dmm/README.md) library (README in Portuguese), a single NuGet package with no
dependencies ([.NET library (NuGet)](#net-library-nuget)).

There are no third-party native dependencies. On Linux the cable is accessed through `hidraw` via
`libc`; on Windows, through `hid.dll`.

> This project is not affiliated with Brymen or Minipa. See the [disclaimer](#disclaimer) at the end.

## Contents

- [Supported hardware](#supported-hardware)
  - [Supported meters](#supported-meters)
  - [Family and dropped requests](#family-and-dropped-requests)
- [Architecture](#architecture)
- [How REA was used](#how-rea-was-used)
- [Prerequisites](#prerequisites)
- [Running on Windows](#running-on-windows)
- [Running on Linux](#running-on-linux)
- [Simulated mode](#simulated-mode)
- [.NET library (NuGet)](#net-library-nuget)
- [REST API](#rest-api)
- [Real-time updates via SignalR](#real-time-updates-via-signalr)
- [Configuration](#configuration)
- [Deployment with systemd and udev (Raspberry Pi and x64)](#deployment-with-systemd-and-udev-raspberry-pi-and-x64)
- [Docker](#docker)
- [Security](#security)
- [Tests](#tests)
- [Troubleshooting](#troubleshooting)
- [Limitations and open questions](#limitations-and-open-questions)
- [Disclaimer](#disclaimer)

## Supported hardware

| Item | Details |
|---|---|
| Multimeters | **BM52x** family (Model ID `52h`, with a datalogger): Brymen **BM525s**, which is the same instrument as the **Minipa ET-2615A**. **BM82x** family (Model ID `82h`, without a datalogger): **Minipa ET-2517A**. Both use the same cable and the same LCD map. |
| Cable | **BC-86X**, optical (infrared) to USB, sold in the **BU-86X** kit. |
| USB | Generic HID, VID **0x0820**, PID **0x0001**, bcdDevice 0x8603, usage page 0xFF00. Manufacturer `Brymen`, product `Superior DMM`. No driver is needed on Windows or Linux. |
| Protocol | Command `00 fam cc` in the output report, where `fam` is the family code (`52` or `82`). `66` reads the real-time data (27 bytes): `52 66` on the BM52x and `82 66` on the BM82x. The BM525s also answers `82 66`, with the same frame, and the ET-2517A does not answer `52 66`. Commands `52 88`, `52 89` and `52 8A` read the BM52x datalogger memory (36 bytes per block); the BM82x has no memory. |
| Tested | BM525s on Windows 11 and on an Orange Pi 5 (Linux arm64); ET-2517A on Windows. Real captures in [`docs/protocol/captures/`](docs/protocol/captures/). |

### Supported meters

| Meter | Family (Model ID) | Datalogger | Status |
|---|---|---|---|
| Brymen **BM525s** = **Minipa ET-2615A** | BM52x (`52h`) | yes | **Validated** on Windows 11 and on an Orange Pi 5 (Linux arm64): real-time reading, memory import, API and library ([validation report](docs/validation-report.md), in Portuguese). |
| **Minipa ET-2517A** | BM82x (`82h`) | no | **Validated** on Windows: identification, `82 66`, decoding of every frame of a sweep through the meter's functions and measurement of the dropped requests ([§8 of the specification](docs/protocol/bm52x-usb-protocol.md#8-família-bm82x-model-id-0x82-e-o-minipa-et-2517a), in Portuguese). |
| Brymen BM521s | BM52x (`52h`, not confirmed by a capture) | yes | Likely, **not validated**. |
| Brymen BM821s, BM822s, BM827s and BM829s | BM82x (`82h`) | no | Likely, **not validated**. The ET-2517A is probably the BM827s under the Minipa brand (same functions, specifications and dimensions, and the Brymen logo in the manual), which has not been confirmed. |
| Brymen BM520 and BM820 (without the “s”) | BM52x and BM82x, from the titles of Brymen's protocol documents | BM820: no; BM520: not confirmed | Neither confirmed nor validated. |

- The Model ID identifies the family, not the model: over USB, an ET-2517A cannot be told apart from a BM829s. The
  library and the API expose the family, with a description that names the validated models
  (`Minipa ET-2615A (Brymen BM525s, BM52x)` and `Minipa ET-2517A (Brymen BM827s, BM82x)`).
- Possible rebrands, **not validated**: Greenlee DM-810A, DM-820A and DM-830A (similar to the BM82x meters) and
  DML-430A (similar to the BM525s), with the DMSC-9U USB kit, which may not use the same HID protocol.
- The BM860s series (BM867s, BM869s) uses the same BU-86X kit, but a different protocol and different software:
  **it is not supported**.
- Version 0.2.0 was validated with the **real ET-2517A** on Windows
  ([§11 of the report](docs/validation-report.md#11-minipa-et-2517a-família-bm82x)): hardware tests, 3 min of
  polling at 200 ms and 1 min at 500 ms without `W7012`, `E9016` or reopening the cable, API (`model` BM82x, SSE,
  SignalR, `E9014` and dashboard) and packages. In that session the meter did not drop any requests, and resending was
  only exercised with drops injected on the host. Still missing: the real BM525s with 0.2.0 (the `82 66` used for
  detection, and the import) and 0.2.0 on a real Linux system ([CHANGELOG](CHANGELOG.md#não-validado), in Portuguese).

### Family and dropped requests

- **Detection.** The 1st query after opening the cable sends `82 66`, which both families accept, and it already counts
  as a reading: bytes 20 to 23 of the frame (the Model ID) give the family. If no report arrives, it tries `52 66`.
  After that, the BM52x is read with `52 66` and the BM82x with `82 66`. A meter swapped on the cable is re-detected
  from the other family's Model ID, or after 3 consecutive queries without a response. The family can also be pinned
  (`Meter:Family` in the API, `BrymenMeterOptions.Family` in the library).
- **Dropped requests.** The ET-2517A drops some of the real-time requests, and the longer the interval between queries,
  the more it drops. Waiting longer does not help: the request is lost, and only resending it works.

  | Interval between queries | 0 ms | 100 ms | 200 ms | 300 ms | 500 ms |
  |---|---|---|---|---|---|
  | Unanswered requests (out of 40) | 0 | 2 | 11 | 11 | 19 |

  The BM525s has never shown this: 5 readings/s, with no gaps, for minutes on end.
- **Resending.** On the BM82x, each query resends the same request, without closing the cable, after 300 ms without
  the 1st report (`Meter:RealTimeResendTimeout`), for up to 2 s (`Meter:RealTimeResendBudget`). Only when this budget
  runs out does the query count as a failure (`W7012`); recovered losses do not raise an error and are counted in
  `realTimeStatistics` of `GET /api/v1/meter`.
- **Datalogger.** Only the BM52x has one. On a BM82x, the import is rejected with `E9014` (409), without any memory
  command: immediately while polling is running (or with `Meter:Family` pinned), and after a real-time query that
  confirms the family when the last frame is more than 2 s old (the meter may have been swapped on the cable).

The details are in [§8 of the specification](docs/protocol/bm52x-usb-protocol.md#8-família-bm82x-model-id-0x82-e-o-minipa-et-2517a)
and in the [library guide](docs/library.md#famílias-bm52x-e-bm82x) (in Portuguese).

## Architecture

The full specification is in [`docs/architecture.md`](docs/architecture.md) (in Portuguese).

```text
BM525s or ──IR── BC-86X ──USB HID── Brymen.Dmm.Hid       hidraw (Linux) / hid.dll (Windows), Bu86xLink
ET-2517A                                  │ 27-byte frames (real time) and 36-byte frames (memory)
                                    Brymen.Dmm.Protocol  RTD → DmmReading (family from the Model ID); blocks → LoggerMemory
                                          │
                                    Brymen.Dmm           BrymenMeter, MeterConnection (family, slow 1st 66,
                                          │              BM82x resending), LoggerMemoryReader (NuGet, net8.0/net10.0)
                                    Brymen.Dmm.Api       polling, acquisition, comparator, import,
                                                         CSV, SSE, SignalR, OpenAPI, health checks, dashboard
```

| Project | Responsibility |
|---|---|
| `src/Brymen.Dmm.Protocol` | Pure decoding, no I/O: 27-byte RTD frame of both families → `DmmReading` (with the family from the Model ID, `MeterFamily`); 36-byte memory blocks → `MemoryBlock`; memory image → `LoggerMemory`. |
| `src/Brymen.Dmm.Hid` | Cross-platform HID transport (`IHidDeviceProvider` / `IHidDevice`) for Linux (hidraw) and Windows (hid.dll with overlapped I/O), simulated meters (the BM525s and the ET-2517A, with the real captures, fault injection and request dropping) and BC-86X framing (`Bu86xLink`, with request resending). |
| `src/Brymen.Dmm` | Library for other .NET projects (NuGet package `Brymen.Dmm`, `net8.0` and `net10.0`): `BrymenMeter` (single, continuous and event-driven reading, reconnection, memory import), `MeterConnection` (opening the cable, family detection, the timeout for the slow 1st `66` and the BM82x resending) and `LoggerMemoryReader` (robust memory-reading algorithm), also used by the API. |
| `src/Brymen.Dmm.Api` | ASP.NET Core minimal API: polling, acquisition, comparator, datalogger import, CSV export, SSE, SignalR hub (`/hubs/meter`), OpenAPI, health checks and the dashboard in `wwwroot/`. |
| `tests/*` | xUnit: protocol vectors, HID framing, API integration tests with the simulated device and, optionally, tests with the real cable and with a virtual cable through `/dev/uhid`. |
| `samples/Brymen.Dmm.SignalRClient` | Console client for the SignalR hub ([Real-time updates via SignalR](#real-time-updates-via-signalr)). |
| `samples/Brymen.Dmm.ConsoleSample`, `samples/Brymen.Dmm.WorkerSample` | Samples that consume the NuGet packages ([.NET library (NuGet)](#net-library-nuget)). |
| `deploy/` | udev rule, systemd unit, install and publish scripts (linux-arm64/x64). The `Dockerfile` and `docker-compose.yml` are at the repository root. |

Reference documents:

- [`docs/protocol/bm52x-usb-protocol.md`](docs/protocol/bm52x-usb-protocol.md): the USB protocol, verified by
  adversarial review and on the real hardware, with [`test-vectors.json`](docs/protocol/test-vectors.json)
  and the [captures](docs/protocol/captures/); §8 covers the BM82x family and the ET-2517A, with the capture scripts in
  [`docs/protocol/tools/`](docs/protocol/tools/);
- [`docs/re/bs8252x-reverse-engineering.md`](docs/re/bs8252x-reverse-engineering.md) (in Portuguese): behavior of
  the original software (CSV and `.g53` formats, timeouts, comparator, import, messages);
- [`docs/validation-report.md`](docs/validation-report.md): integration validation (build, tests, package sizes
  and end-to-end tests with the real BM525s on Windows and on an Orange Pi 5);
- [`CHANGELOG.md`](CHANGELOG.md): the changes in each version.

## How REA was used

The behavior of the original was established through **static reverse engineering**: the `.exe` was not
run and the cable was not used at this stage. The main tool was **REA (Reverse Engineer Anything) 6.7.0**.
Summary (details, exact commands and results in
[§3 of the RE report](docs/re/bs8252x-reverse-engineering.md#3-como-o-rea-foi-usado)):

- `rea inspect-artifact` and `inspect-managed-artifact` classified the binary as native PE32, with no
  .NET metadata. The strings and the linker showed that it is a C++Builder 5 program with VCL.
- `rea inspect-pe-resources` extracted the 116 resources: the VCL forms (RCDATA), the string table with
  the W/E messages, the icons and the VERSIONINFO.
- `rea inspect-with-objdump`, with `llvm-objdump` configured through `REA_OBJDUMP_COMMAND`, provided the
  headers, the sections and the disassembly. REA truncates the output at 16 MiB.
- The deep-analysis providers (Ghidra, IDA, Hopper, Rizin) were not available, so `analyze`,
  `function` and `decompile` could not be used. The analysis was therefore completed with `llvm-objdump`
  and `dumpbin` directly and with custom Python scripts: decoding of the forms, string table, scanning
  of the VCL VMTs, switch resolution and a reimplementation of the decoding, checked against the captures.

The results are the program's 27 modules, 147 UI handlers with their addresses and the complete HID I/O
path (timeouts, retries, `82 66` × `52 66`). The file formats (CSV, `.g53`, `.TMP`, `BTCDL.ini`), the
comparator and the messages came from there as well. An
[independent adversarial review](docs/re/bs8252x-reverse-engineering.md#16-revisão-adversarial-independente)
checked the critical points in the disassembly. The parity requirement lists (P0/P1/P2) in §13
guided this port.

## Prerequisites

- **.NET SDK 10.0** (10.0.100 or newer in the 10.0 line, as set in [`global.json`](global.json)) to
  build, test and publish. Any feature band works, including Ubuntu's `dotnet-sdk-10.0` package (10.0.1xx).
- **Windows 10/11** (x64) for development: the cable uses the system's HID driver.
- **Linux arm64 or x64** with glibc, a kernel with `hidraw` (`CONFIG_HIDRAW`) and, for the service, systemd.
  On the Raspberry Pi use a **64-bit** OS: `dpkg --print-architecture` must show `arm64`. `uname -m`
  does not work for this: it shows the kernel, which is `aarch64` even on the 32-bit Raspberry Pi OS
  (armhf) on the Pi 4/400 and the Pi 5. No 32-bit armv7/armhf build is published.
- A **self-contained** publish does not require .NET to be installed on the target.
- For the container image (optional): Docker with **buildx** (BuildKit). Without it, the legacy builder builds the
  image for the machine's own architecture ([Docker](#docker)).

## Running on Windows

```powershell
dotnet run --project src/Brymen.Dmm.Api
```

- Dashboard: <http://localhost:5080/>. OpenAPI document: <http://localhost:5080/openapi/v1.json>.
  The address comes from the `Urls` key (default `http://0.0.0.0:5080`); check the `Now listening on`
  line in the console.
- The cable shows up in Device Manager as "HID-compliant device"
  (`VID_0820&PID_0001`). Do not install a driver.
- Close the original software first: two programs polling the same cable get their responses mixed up.

## Running on Linux

1. Install the udev rule, so the cable can be used without root:

   ```bash
   sudo groupadd --system brymen-dmm             # the group named in the rule must exist
   sudo install -m 0644 deploy/linux/99-brymen-bu86x.rules /etc/udev/rules.d/
   sudo udevadm control --reload
   # re-apply the rules only to the cable's hidraw (or unplug and replug the cable)
   for n in /sys/class/hidraw/hidraw*; do
       grep -qix 'HID_ID=0003:00000820:00000001' "$n/device/uevent" &&
           sudo udevadm trigger --action=change --subsystem-match=hidraw --sysname-match="${n##*/}"
   done
   ls -l /dev/brymen-bu86x                       # symlink to /dev/hidrawN
   ```

   The loop finds the cable by the `HID_ID` of the HID device (USB bus, VID 0820, PID 0001). Without it, a
   `udevadm trigger --subsystem-match=hidraw` would send a `change` event to every hidraw node on the machine, and the
   rules of the other HID devices would run again.

   The rule grants access to the local session user (`uaccess`). In an SSH session, join the group:
   `sudo usermod -aG brymen-dmm "$USER"` and open a new session.

2. Run the API:

   ```bash
   dotnet run --project src/Brymen.Dmm.Api
   # optional: pin the device
   Meter__DevicePath=/dev/brymen-bu86x dotnet run --project src/Brymen.Dmm.Api
   ```

To run it as a service, see [Deployment with systemd and udev](#deployment-with-systemd-and-udev-raspberry-pi-and-x64).

## Simulated mode

With `Meter:Transport` = `Simulated`, the API uses a simulated meter that replays the real captures embedded in the
executable. `Meter:SimulatedModel` selects which one:

- `Bm525s` (default): `52 66` and `82 66` cycle through the frames of
  [`rtd-acv.txt`](docs/protocol/captures/rtd-acv.txt) (ACV with Hz on the secondary display), and the import reads
  [`memory-dump-1.txt`](docs/protocol/captures/memory-dump-1.txt), with 1 session and 375 samples;
- `Et2517a`: `82 66` steps through the 43 real frames of the ET-2517A sweep
  ([`et2517a-sweep-2026-10-23.txt`](docs/protocol/captures/et2517a-sweep-2026-10-23.txt): ACV, DCV, Hz, continuity,
  temperature, diode, manual range and REC), without a datalogger and with the request dropping measured on the real
  meter; the resends show up in `realTimeStatistics` of `GET /api/v1/meter`.

It works on any OS and without the cable, and it is the mode used by the integration tests. The simulated meters are
`SimulatedBm52xMeter.FromCaptures()` and `SimulatedBm52xMeter.FromEt2517aCaptures()` from the `Brymen.Dmm.Hid`
library, with per-command fault injection (`FaultInjector`) and, on the BM525s, an `8A` that, right after an `88`,
advances like an `89`, as on the real meter.

```bash
Meter__Transport=Simulated dotnet run --project src/Brymen.Dmm.Api
Meter__Transport=Simulated Meter__SimulatedModel=Et2517a dotnet run --project src/Brymen.Dmm.Api
```

```powershell
$env:Meter__Transport = 'Simulated'; dotnet run --project src/Brymen.Dmm.Api
# or, from the command line:
dotnet run --project src/Brymen.Dmm.Api -- --Meter:Transport=Simulated --Meter:SimulatedModel=Et2517a
```

With `Auto` (the default), the service uses the platform's HID and falls back to the simulated meter only when
the platform has no HID transport. On Linux or Windows without the cable, `Auto` does **not** fall back to the
simulated meter: the API returns 503 (`E9015`) until the cable shows up.

## .NET library (NuGet)

To integrate the meter into other .NET 8 or newer projects without the API, there are two packages, targeting
`net8.0` and `net10.0`:

- [`Brymen.Dmm`](src/Brymen.Dmm/README.md): `BrymenMeter` (single, continuous or event-driven reading, with
  automatic reconnection, family detection and resending of the requests dropped by the BM82x, and BM52x datalogger
  import with the same algorithm as the API) and the simulated meters. A single package with no dependencies that
  bundles `Brymen.Dmm.Protocol` and `Brymen.Dmm.Hid`.
- [`Brymen.Dmm.Extensions.Hosting`](src/Brymen.Dmm.Extensions.Hosting/README.md) (optional; README in Portuguese):
  `AddBrymenMeter` for the Generic Host, with a hosted service and `IBrymenMeterAccessor`.

```csharp
await using var meter = await BrymenMeter.OpenAsync();      // E9015 without the cable; OpenSimulatedAsync() without hardware
await foreach (var r in meter.ReadContinuouslyAsync())      // every 200 ms, with automatic reconnection
    Console.WriteLine($"{r.Family} {r.Main?.Text} {r.Main?.UnitSymbol}");   // Bm52x or Bm82x, from the Model ID
```

```bash
dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages                      # Brymen.Dmm.<version>.nupkg and .snupkg
dotnet pack src/Brymen.Dmm.Extensions.Hosting -c Release -o artifacts/packages
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --simulated --count 10   # consumes the package from the local feed
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --simulated et2517a --count 10
```

The complete guide, covering the public API, threading, errors and codes, lifecycle, trimming/AOT, Linux and
Windows, and packaging, is in [`docs/library.md`](docs/library.md). The
[`ConsoleSample`](samples/Brymen.Dmm.ConsoleSample/README.md) and [`WorkerSample`](samples/Brymen.Dmm.WorkerSample/README.md)
samples (READMEs in Portuguese) consume the packages from the local feed `artifacts/packages`. The packages are MIT,
and the changes in each version are in the [CHANGELOG](CHANGELOG.md).

## REST API

Base: `http://<host>:5080`. The exact schema of each request and response is in
`GET /openapi/v1.json`.

| Method | Route | Description | Original |
|---|---|---|---|
| `GET` | `/` | Minimal web dashboard, with the detected meter in the title | Digital/Analog Meter |
| `GET` | `/api/v1/meter` | Link state (`unlinked`, `connecting`, `connected`, `reconnecting`...), cable state, last error, detected meter (`model`) and real-time counters (`realTimeStatistics`) | — |
| `PUT` | `/api/v1/meter/link` | `{ "enabled": true }` turns polling on (one real-time query every `Meter:PollInterval`: `82 66` during detection, then `52 66` on the BM52x and `82 66` on the BM82x); `false` turns it off | Link/Unlink |
| `GET` | `/api/v1/readings/latest` | Latest reading: displays, annunciators and comparator result. `409` with `E9002` when the link is disabled (`503` with `E9015` if Link failed because the cable is missing) and with `CONFLICT` during an import | Digital Meter |
| `GET` | `/api/v1/readings/stream` | Server-Sent Events, one `reading` event per reading and a `heartbeat` every 15 s | Digital/Analog Meter |
| — | `/hubs/meter` | SignalR hub: `reading`, `status` and `acquisitionSample` events ([details](#real-time-updates-via-signalr)) | Digital/Analog Meter |
| `GET` | `/api/v1/acquisition` | Recording state: mode, samples, capacity, backup and Max/Min (per unit and coupling: they reset when the meter's function changes) | Graphical Recorder / Data List |
| `GET`, `PUT` | `/api/v1/acquisition/settings` | Mode `fullSpeed` (every reading), `interval` (N whole seconds) or `manual`; `PUT` returns `409` while a recording is in progress | Interval / Customization... |
| `POST` | `/api/v1/acquisition/start`, `/stop` | Starts (requires the link: `409` with `E9002`) and stops recording | Start/Stop |
| `POST` | `/api/v1/acquisition/samples` | Takes one manual sample (`manual` mode, recording started); `409` during an import | Manual |
| `GET` | `/api/v1/acquisition/samples` | Samples in the buffer, paginated (`offset`, `limit`) | Data List |
| `GET` | `/api/v1/acquisition/samples.csv` | CSV export with the original's header; `?format=extended` adds ISO-8601 and SI columns | Export → Text/Data |
| `DELETE` | `/api/v1/acquisition/samples` | Clears the buffer | Clear |
| `GET` | `/api/v1/comparator` | Hi/Lo limits and alarm | Comparator |
| `PUT` | `/api/v1/comparator` | Changes and persists the limits (`settings.json`); omitted fields are left as they are | Comparator |
| `POST` | `/api/v1/logger/imports` | Starts the datalogger import: `202 Accepted` + `Location`; `409` if another one is in progress (`CONFLICT`) or on a meter without a datalogger (`E9014`, BM82x family) | Import data from DMM |
| `GET` | `/api/v1/logger/imports` | Stored imports (the `Logger:ImportRetention` most recent ones) | — |
| `GET`, `DELETE` | `/api/v1/logger/imports/{id}` | Progress (`running`, `completed`, `failed`, `canceled`); `DELETE` cancels or deletes | — |
| `GET` | `/api/v1/logger/imports/{id}/image` | Raw memory image (24-byte blocks) | — |
| `GET` | `/api/v1/logger/imports/{id}/sessions` | Imported sessions (`.../sessions/{n}` for a single one, `n` starting at 1) | Data List |
| `GET` | `/api/v1/logger/imports/{id}/sessions/{n}/samples` | Samples of one session (JSON, paginated) | Data List |
| `GET` | `/api/v1/logger/imports/{id}/sessions/{n}/samples.csv` | Samples of one session (CSV); `MainData`/`Sub Data` with 4 digits and leading zeros, like the original (`02.87`) | Export |
| `GET` | `/health/live` | Process is up | — |
| `GET` | `/health/ready` | Ready when the cable is open or the transport is simulated | — |
| `GET` | `/openapi/v1.json` | OpenAPI document | — |

The image and session routes return `409` (`CONFLICT`) while the import is in progress or if it did not
complete successfully. The full list of responses per route is in
[`docs/architecture.md`](docs/architecture.md#rotas).

**Dashboard.** The title of the dashboard at `/` shows the detected meter (`Minipa ET-2517A (Brymen BM827s, BM82x)`, for example;
“Brymen DMM” before the 1st reading), with the family, the Model ID and the real-time resends in the tooltip. On a
meter without a datalogger, the “Importar do medidor” (“Import from meter”) button disappears, and a note says that
there is no memory to import.

**Errors.** Failures are returned as `ProblemDetails` (RFC 9457), with the code in `code`: either the code of
the original software (W/E) or an API code. A missing cable returns **503**.

```json
{ "status": 503, "title": "Cabo adaptador USB não encontrado", "detail": "Cabo adaptador USB não encontrado.", "code": "E9015" }
```

| Code | Meaning | HTTP |
|---|---|---|
| `E9015` | USB cable not found | 503 |
| `E9016` | Transmission timed out (3rd consecutive real-time failure, or import) | 503 |
| `E9017` | Meter turned off, disconnected or without a datalogger | 503 |
| `E9014` | Meter without a datalogger (BM82x family, such as the ET-2517A): the import is rejected without any memory command and without creating an import. A library code, not one from the original | 409 |
| `W7012` | No data received (real time; on the BM82x, after the resends run out) | 503 |
| `W8011` | Invalid frame received from the meter | 503 |
| `E9002` | Link disabled (recording or reading without a link) | 409 |
| `E9003` | Failed to create or write the backup file (shown in `backupError` of `GET /api/v1/acquisition`) | — |
| `E9007`, `E9008`, `W7010` | Invalid value, unacceptable limit, Hi ≤ Lo | 400 |
| `W8000` | Low multimeter battery (`LOW_BATTERY` annunciator in the reading) | — |
| `FORBIDDEN` | `POST`, `PUT`, `DELETE` or hub WebSocket coming from a page on another origin ([Security](#security)) | 403 |
| `NOT_FOUND` | Nonexistent route, import or session | 404 |
| `METHOD_NOT_ALLOWED` | Existing route with a different HTTP method | 405 |
| `VALIDATION` | Invalid body or parameter (malformed JSON, `offset`, `limit`, `format`) | 400 |
| `CONFLICT` | Not allowed in the current state: import or recording in progress, buffer full | 409 |
| `INTERNAL` | Internal error; the response does not include exception details | 500 |

### curl examples

```bash
H=http://localhost:5080

# State, detected meter and link
curl -s "$H/api/v1/meter"
curl -s -X PUT "$H/api/v1/meter/link" -H 'Content-Type: application/json' -d '{"enabled":true}'

# Latest reading and SSE stream (-N turns off curl's buffering)
curl -s "$H/api/v1/readings/latest"
curl -N "$H/api/v1/readings/stream"
# event: reading
# id: 42
# data: {"sequence":42,"timestamp":"…","main":{"text":"0.287","value":0.287,"unit":"V","mode":"AC",…},"secondary":{"text":"0.000","unit":"Hz",…},"flags":["AUTO",…],"comparator":null,"rawHex":"…"}
```

The SSE stream starts with the latest known reading and sends a `heartbeat` event, with no reading, every
`Sse:HeartbeatInterval` (15 s), so that proxies do not close the idle connection during an import or a
cable failure. It ends when the service shuts down.

**Detected meter.** `GET /api/v1/meter` returns, in `model`, the family identified by the real-time Model ID (`null`
before the 1st reading and after 3 consecutive queries without a response), and, in `realTimeStatistics`, the
real-time counters since the service started. With the simulated ET-2517A (`Meter:SimulatedModel=Et2517a`):

```json
{
  "linkEnabled": true, "state": "connected", "transport": "simulated", "...": "...",
  "model": { "family": "BM82x", "modelId": "0x82", "name": "Minipa ET-2517A (Brymen BM827s, BM82x)", "supportsDataLogger": false },
  "realTimeStatistics": { "queries": 27, "requests": 33, "resends": 6, "lostRequests": 6, "recoveredQueries": 4, "failedQueries": 0 }
}
```

`family` is `BM52x` or `BM82x`; `modelId`, `0x52` or `0x82` (the same code as in the real-time request); `name` lists
the validated models of the family; and `supportsDataLogger = false` means that the import returns 409 with `E9014`.
The counters are the library's ([`RealTimeStatistics`](docs/library.md#pedidos-descartados-e-reenvio)): queries,
requests sent, resends, lost requests, queries recovered by resending and queries without a response (each one a
`W7012`). The SignalR `status` event is also sent when the family changes.

Recording (Graphical Recorder) and CSV export:

```bash
curl -s -X PUT "$H/api/v1/acquisition/settings" -H 'Content-Type: application/json' \
     -d '{"mode":"interval","intervalSeconds":5}'          # or "fullSpeed" / "manual"
curl -s -X POST "$H/api/v1/acquisition/start"
curl -s "$H/api/v1/acquisition"                            # state, samples, Max/Min
curl -s -X POST "$H/api/v1/acquisition/samples"            # manual mode only
curl -s "$H/api/v1/acquisition/samples?offset=0&limit=100"
curl -s -X POST "$H/api/v1/acquisition/stop"
curl -s -o gravacao.csv "$H/api/v1/acquisition/samples.csv"
curl -s "$H/api/v1/acquisition/samples.csv?format=extended"
curl -s -X DELETE "$H/api/v1/acquisition/samples"
```

The CSV follows the original: comma separator, CRLF line endings, `#item` starting at 1, `Time` as the
elapsed time in the `%3d:%02d:%02d.%1d` format, data as shown on the display and `Ohm` instead of Ω:

```text
#item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc
1,  0:00:00.0,0.287,V,AC,0.000,Hz,
2,  0:00:05.0,0.156,V,AC,0.000,Hz,
```

The extended format adds `Timestamp,MainSiValue,SubSiValue,Flags,Comparator`. During a recording, each
sample also goes to the `~BDMDLn.csv` backup in `<DataDirectory>/backup`.

Some deviations from the original are deliberate ([details](docs/architecture.md#divergências-deliberadas-em-relação-ao-original)):

- `Time` is the **elapsed time** since the 1st sample, not the wall-clock time, which on a Raspberry Pi without
  an RTC jumps when NTP sets the clock; the absolute time is in `Timestamp`;
- `fullSpeed` records **every reading** from polling (every `Meter:PollInterval`, 200 ms by default), not a
  500 ms timer; for the original's pace, use `Meter__PollInterval=00:00:00.500`;
- on the **3rd consecutive real-time failure**, the error is `E9016` and the service keeps retrying every
  `Meter:ReconnectDelay`; the original showed "No data received." and dropped the link. On the BM82x, a failure is a
  query whose resends have run out, not a lost request;
- the original polls the real-time data with `82 66` on both families, waits up to 4 s and closes and reopens the cable
  when the request is lost; the port uses `52 66` on the BM52x after detection and, on the BM82x, resends the request
  every 300 ms without closing the cable;
- on a BM82x, the import is rejected with `E9014`, not with the original's `E9017`.

Comparator. The limits apply to the SI value of the main display (`0.5` = 0.5 V, not 0.5 mV);
`v > Hi` gives `High`, `v < Lo` gives `Low` and anything else is `Pass`:

```bash
curl -s "$H/api/v1/comparator"
curl -s -X PUT "$H/api/v1/comparator" -H 'Content-Type: application/json' \
     -d '{"enabled":true,"high":0.5,"low":0.1,"alarm":false}'
```

Datalogger import (BM52x only). Polling is paused while it runs, and `GET /api/v1/readings/latest`
returns `409` (`CONFLICT`). The reader waits 200 ms after the `88`, recovers from a failed `88` with another
`88` and only uses `8A` after an `89` (§2.7 and §4.11 of the specification). On the real BM525s, importing 2272
bytes takes about 11 s. Afterwards, the meter takes about 2 s to answer the first `52 66`, which comes back with
dashes on the displays (`-.---`, null `value`); polling waits up to 3 s for this response without reporting an error.
On a meter of the BM82x family (`model.supportsDataLogger = false`), the `POST` returns `409` with `E9014`, without
creating an import or sending any memory command: immediately while polling is running, and, when the family is still
unknown (link disabled since startup, for example) or was identified more than 2 s ago, after a real-time query that
confirms it (a BM525s put in place of the ET-2517A is imported normally):

```bash
loc=$(curl -s -D - -o /dev/null -X POST "$H/api/v1/logger/imports" | tr -d '\r' | sed -n 's/^[Ll]ocation: //p')
curl -s "$H$loc"                                           # progress; repeat until "completed"
curl -s "$H$loc/sessions"
curl -s "$H$loc/sessions/1/samples?limit=1000"
curl -s -o sessao1.csv "$H$loc/sessions/1/samples.csv"
curl -s -o memoria.bin "$H$loc/image"
```

## Real-time updates via SignalR

The SignalR hub **pushes every new multimeter reading** to all connected clients, without them having to
poll the API. The source is the same as for SSE: the hub adds no extra polling of the cable.

- Endpoint: **`/hubs/meter`** (e.g. `http://raspberrypi.local:5080/hubs/meter`), JSON protocol, with the
  same field names (camelCase) and enums as the REST routes.
- No authentication, like the rest of the API ([Security](#security)).

| Event (server → client) | Payload | When |
|---|---|---|
| `reading` | The same JSON as `GET /api/v1/readings/latest` | On every decoded reading (each valid real-time frame), to all clients |
| `status` | The same JSON as `GET /api/v1/meter` | When the link, the cable or the meter changes: connected, disconnected, error (with `lastError.code`), start and end of an import, detected family (`model`) |
| `acquisitionSample` | One sample, like the items of `GET /api/v1/acquisition/samples` | On every sample recorded by the Graphical Recorder |

| Method (client → server) | Returns |
|---|---|
| `GetLatest()` | The latest reading, or `null` when `GET /api/v1/readings/latest` would return an error (link disabled, import, failure) |
| `GetStatus()` | The meter state |

The hub does not resend what a client missed while it was disconnected. Call `GetStatus()` and `GetLatest()`
after connecting and after each reconnection; a jump in the `sequence` field indicates lost readings. If the
clients cannot keep up, the server drops the oldest readings from the queue (and logs the count) instead of
delaying the polling.

### C# client

NuGet package `Microsoft.AspNetCore.SignalR.Client`:

```csharp
using Microsoft.AspNetCore.SignalR.Client;

await using var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost:5080/hubs/meter")
    .WithAutomaticReconnect()
    .Build();

connection.On<Reading>("reading", r =>
    Console.WriteLine($"#{r.Sequence} {r.Main?.Text} {r.Main?.UnitSymbol} {r.Main?.Mode} | {r.Secondary?.Text} {r.Secondary?.UnitSymbol}"));
connection.On<Status>("status", s => Console.WriteLine($"medidor: {s.State} {s.Model?.Name} {s.LastError?.Code}"));
connection.Reconnected += async _ => Console.WriteLine($"reconectado; última: {(await connection.InvokeAsync<Reading?>("GetLatest"))?.Sequence}");

await connection.StartAsync();
Console.WriteLine($"medidor: {(await connection.InvokeAsync<Status>("GetStatus")).State}");
Console.ReadLine();

// Only the fields used here; the others are ignored during deserialization.
record Display(string Text, double? Value, string UnitSymbol, string Mode);
record Reading(long Sequence, DateTimeOffset Timestamp, Display? Main, Display? Secondary, string[] Flags);
record MeterError(string Code, string Message);
record Model(string Family, string ModelId, string Name, bool SupportsDataLogger);
record Status(bool LinkEnabled, string State, MeterError? LastError, bool Importing, Model? Model);
```

The [`samples/Brymen.Dmm.SignalRClient`](samples/Brymen.Dmm.SignalRClient) project is a ready-to-use console
client that shows the readings and the state changes:

```bash
dotnet run --project samples/Brymen.Dmm.SignalRClient -- http://localhost:5080
```

The argument is the API address (the hub address, `.../hubs/meter`, also works); with no argument, the sample
uses `http://localhost:5080`.

### JavaScript client

npm package `@microsoft/signalr` (`npm install @microsoft/signalr`):

```js
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/meter')          // from another origin: 'http://raspberrypi.local:5080/hubs/meter' + CORS
  .withAutomaticReconnect()
  .build();

connection.on('reading', r => console.log(r.sequence, r.main?.text, r.main?.unitSymbol, r.secondary?.text));
connection.on('status', s => console.log('medidor:', s.state, s.model?.name ?? '', s.lastError?.code ?? ''));
connection.on('acquisitionSample', s => console.log('amostra', s.item, s.time, s.main?.text));
connection.onreconnected(async () => console.log('última:', await connection.invoke('GetLatest')));

await connection.start();
console.log('medidor:', await connection.invoke('GetStatus'));
```

### CORS

A page served by the API itself (same origin) does not need CORS. For a **browser** client on
another origin (another host or another port), allow the origin in `Cors:AllowedOrigins`. The policy uses
`AllowCredentials`, which the SignalR JavaScript client requires, and therefore does not accept `*`: list each
origin with scheme, host and port.

```bash
Cors__AllowedOrigins__0=http://192.168.0.10:8080 Cors__AllowedOrigins__1=http://localhost:5173 \
  dotnet run --project src/Brymen.Dmm.Api
```

CORS only tells the browser whether it may read the responses; it is not access control and does not block
clients outside the browser (C#, `curl`). State-changing requests and hub WebSockets coming from a page on another
origin are rejected by the API (403 `FORBIDDEN`), unless the origin is in `Cors:AllowedOrigins`
([Security](#security)).

## Configuration

Configuration comes from `appsettings.json`, from environment variables (`:` becomes `__`, as in
`Meter__Transport`) and from the command line (`--Meter:Transport=Simulated`).

`appsettings.json` and `wwwroot/` are read from the content root, which is the **current directory**, not
the executable's folder. `dotnet run --project` uses the project folder, and the systemd unit sets
`ASPNETCORE_CONTENTROOT=/opt/brymen-dmm`. When running the published executable by hand, change into its
folder or set `ASPNETCORE_CONTENTROOT`. Otherwise `appsettings.json` is ignored: the API listens on
`http://localhost:5000`, and the dashboard's `/` returns 404 (`/index.html` and the other files in `wwwroot/`
still respond).

```jsonc
{
  "Urls": "http://0.0.0.0:5080",
  "Meter": {
    "Transport": "Auto",          // Auto | Hid | Simulated
    "DevicePath": null,           // e.g. "/dev/hidraw0"; null = first VID 0820/PID 0001
    "AutoLink": true,             // opens the cable and starts polling at startup
    "Family": "Auto",             // Auto | Bm52x | Bm82x
    "PollInterval": "00:00:00.200",
    "ReportTimeout": "00:00:01",
    "RealTimeResendTimeout": "00:00:00.300",
    "RealTimeResendBudget": "00:00:02",
    "ReconnectDelay": "00:00:02",
    "SimulatedModel": "Bm525s"    // Bm525s | Et2517a (only with Transport = Simulated)
  },
  "Acquisition": { "MaxSamples": 1000000, "BackupDirectory": null, "BackupRetention": 20 },
  "Logger": { "ImportRetention": 10 },
  "SignalR": { "EnableDetailedErrors": false, "KeepAliveInterval": "00:00:15" },
  "Sse": { "HeartbeatInterval": "00:00:15" },
  "Cors": { "AllowedOrigins": [] },  // e.g. [ "http://192.168.0.10:8080" ]
  "DataDirectory": null,          // default: <ContentRoot>/data
  "AllowedHosts": "*"             // e.g. "raspberrypi.local;192.168.0.10;localhost"
}
```

| Key | Environment variable | Default | Description |
|---|---|---|---|
| `Urls` | `Urls` | `http://0.0.0.0:5080` | Kestrel listening addresses |
| `AllowedHosts` | `AllowedHosts` | `*` | Names and IPs accepted in the `Host` header, separated by `;` (protection against DNS rebinding, [Security](#security)) |
| `Meter:Transport` | `Meter__Transport` | `Auto` | `Auto`, `Hid` or `Simulated` |
| `Meter:DevicePath` | `Meter__DevicePath` | `null` | `/dev/hidrawN`, `/dev/brymen-bu86x` or the Windows HID path; `null` uses the first VID 0820/PID 0001 device |
| `Meter:AutoLink` | `Meter__AutoLink` | `true` | Opens the cable and starts polling at startup |
| `Meter:Family` | `Meter__Family` | `Auto` | `Auto` detects the family on the 1st reading (`82 66`); `Bm52x` (always `52 66`, like version 0.1.0) and `Bm82x` (always `82 66`) pin it, and a meter of the other family gives `W8011` ([Family and dropped requests](#family-and-dropped-requests)) |
| `Meter:PollInterval` | `Meter__PollInterval` | `00:00:00.200` | Interval between the starts of two consecutive real-time queries (the original does not pause); on the ET-2517A, a shorter interval loses fewer requests |
| `Meter:ReportTimeout` | `Meter__ReportTimeout` | `00:00:01` | Maximum wait for an HID report |
| `Meter:RealTimeResendTimeout` | `Meter__RealTimeResendTimeout` | `00:00:00.300` | BM82x and detection: wait for the 1st report before resending the same request, without closing the cable (10 ms to 30 s) |
| `Meter:RealTimeResendBudget` | `Meter__RealTimeResendBudget` | `00:00:02` | BM82x and detection: total time of a query, resends included; only when it runs out does the query end in `W7012` (10 ms to 1 min) |
| `Meter:ReconnectDelay` | `Meter__ReconnectDelay` | `00:00:02` | Wait before reopening the cable after a failure |
| `Meter:SimulatedLatency` | `Meter__SimulatedLatency` | `00:00:00.020` | Response latency of the simulated transport |
| `Meter:SimulatedModel` | `Meter__SimulatedModel` | `Bm525s` | Meter of the simulated transport: `Bm525s` (with a datalogger) or `Et2517a` (without a datalogger, with the real meter's request dropping) ([Simulated mode](#simulated-mode)) |
| `Acquisition:MaxSamples` | `Acquisition__MaxSamples` | `1000000` | Buffer capacity; when it is full, recording stops |
| `Acquisition:BackupDirectory` | `Acquisition__BackupDirectory` | `null` | Continuous CSV backup (equivalent to `~BDMDLn.TMP`); `null` = `<DataDirectory>/backup` |
| `Acquisition:BackupEnabled` | `Acquisition__BackupEnabled` | `true` | Also writes each sample to the backup |
| `Acquisition:BackupRetention` | `Acquisition__BackupRetention` | `20` | How many `~BDMDLn.csv` backups are kept in the folder (1 to 100000); when a new one is created, the lower-numbered ones are deleted |
| `Logger:ImportRetention` | `Logger__ImportRetention` | `10` | How many finished imports are kept, in memory and in `imports/` (1 to 1000); when a new one finishes, and at startup, the oldest ones are deleted |
| `SignalR:EnableDetailedErrors` | `SignalR__EnableDetailedErrors` | `false` | Sends hub exception messages to the client (for diagnostics only) |
| `SignalR:KeepAliveInterval` | `SignalR__KeepAliveInterval` | `00:00:15` | Hub ping interval (1 s to 10 min); the client's `serverTimeout` (30 s by default) must be at least twice this value |
| `Sse:HeartbeatInterval` | `Sse__HeartbeatInterval` | `00:00:15` | Interval of the SSE `heartbeat` event |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0`, `__1`... | empty | Allowed browser client origins, with `AllowCredentials` ([CORS](#cors)); empty disables CORS |
| `DataDirectory` | `DataDirectory` | `null` | Data folder; `null` = `<ContentRoot>/data` |

`DataDirectory` holds `settings.json` with the comparator limits (equivalent to `BTCDL.ini`), the raw
import images (`imports/`) and the recording backup (`backup/`). Each recording that is started
creates a new backup, `~BDMDL<n>.csv`, in the extended CSV format (with the timestamp of each sample).

**Precedence**, from highest to lowest: command line, environment variables without a prefix,
`appsettings.{Environment}.json`, `appsettings.json` and, last, the `ASPNETCORE_*` and `DOTNET_*`
variables. That is why the `Urls` key in `appsettings.json` takes precedence over `ASPNETCORE_URLS`; to change the port
through the environment, use `Urls`.

### Memory and `Acquisition:MaxSamples`

The recording buffer is kept in memory. Measured on x64 (arm64 has the same object layout), each sample
takes about **130 bytes** with both displays steady, **280 bytes** when the main display changes on every
reading and **430 bytes** when both change. With the default of 1,000,000 samples, that is 120 to
410 MiB for the buffer alone.

On boards with **512 MB of RAM** (Raspberry Pi Zero 2 W, Pi 3 A+), lower the limit, for example to
`Acquisition__MaxSamples=200000` (about 85 MiB in the worst case; in `fullSpeed` with the default `PollInterval`,
about 11 h of recording). When the buffer is full, recording stops; the on-disk backup has the samples of the
recording. For longer recordings, use `interval` mode or export and clear the buffer from time to time
(`DELETE /api/v1/acquisition/samples` continues the recording in a new backup).

## Deployment with systemd and udev (Raspberry Pi and x64)

Deployment uses a self-contained, single-file, trimmed package. .NET does not need to be installed on the target.

| File | Purpose |
|---|---|
| [`deploy/publish.ps1`](deploy/publish.ps1), [`deploy/publish.sh`](deploy/publish.sh) | `dotnet publish` for `linux-arm64` and `linux-x64` and packaging as `tar.gz` |
| [`deploy/linux/install.sh`](deploy/linux/install.sh) | Installs or updates (idempotent) |
| [`deploy/linux/uninstall.sh`](deploy/linux/uninstall.sh) | Removes; `--purge` also deletes the data and the user |
| [`deploy/linux/99-brymen-bu86x.rules`](deploy/linux/99-brymen-bu86x.rules) | udev rule: group `brymen-dmm`, mode 0660, `uaccess` and the `/dev/brymen-bu86x` symlink |
| [`deploy/linux/brymen-dmm.service`](deploy/linux/brymen-dmm.service) | `Type=notify` unit (`UseSystemd`) with hardening |
| [`deploy/linux/brymen-dmm.env`](deploy/linux/brymen-dmm.env) | Template for `/etc/brymen-dmm/brymen-dmm.env` (local settings) |

### 1. Publish (on the development machine)

```powershell
./deploy/publish.ps1                          # linux-arm64 and linux-x64
./deploy/publish.ps1 -Rid linux-arm64         # Raspberry Pi only
./deploy/publish.ps1 -Rid linux-x64 -FrameworkDependent
```

```bash
./deploy/publish.sh                           # linux-arm64 and linux-x64
./deploy/publish.sh --rid linux-arm64
./deploy/publish.sh --rid linux-x64 --framework-dependent
```

The publish output goes to `artifacts/publish/<rid>/` and the package to
`artifacts/brymen-dmm-<version>-<rid>.tar.gz`. The version comes from `<Version>` in `Directory.Build.props`.
The package contains the `Brymen.Dmm.Api` executable, `appsettings.json`, `wwwroot/`, `README.md` and the
`deploy/linux/` folder.

The self-contained executable bundles the **10.0.13** .NET and ASP.NET Core runtime even with an SDK that ships an
older one (10.0.401 ships 10.0.12): `Microsoft.AspNetCore.SignalR.Common` 10.0.12 is affected by the security advisory
GHSA-334f-vvp4-7g6p. The 10.0.13 runtime packs come from NuGet during restore (the `UsePatchedRuntimePacks` target
in `Brymen.Dmm.Api.csproj`); with an SDK that already ships 10.0.13 or newer, the SDK's runtime is used. The
framework-dependent mode uses the runtime installed on the target: keep it up to date.

The framework-dependent mode adds the `-fd` suffix to the names and does not trim. It requires the
ASP.NET Core Runtime 10 to be installed globally on the target, from the distribution's package or in
`/usr/share/dotnet`. If it is somewhere else, set `DOTNET_ROOT` in `/etc/brymen-dmm/brymen-dmm.env`. A
runtime under `$HOME` is invisible to the service because of `ProtectHome`.

### 2. Install on the Raspberry Pi (arm64)

```bash
scp artifacts/brymen-dmm-0.2.0-linux-arm64.tar.gz pi@raspberrypi.local:
ssh pi@raspberrypi.local
tar -xzf brymen-dmm-0.2.0-linux-arm64.tar.gz
sudo bash brymen-dmm-0.2.0-linux-arm64/deploy/linux/install.sh brymen-dmm-0.2.0-linux-arm64
```

`install.sh` also accepts the `.tar.gz` itself. From a clone of the repository, run
`sudo bash deploy/linux/install.sh artifacts/brymen-dmm-0.2.0-linux-arm64.tar.gz`. It:

1. checks that the executable is ELF and that the system has its interpreter (`/lib/ld-linux-aarch64.so.1` or
   `/lib64/ld-linux-x86-64.so.2`), which prevents installing the x64 package on the Pi, or the arm64 one on a 32-bit
   Raspberry Pi OS, where the kernel is arm64 but the userland is armhf;
2. creates the `brymen-dmm` system group and user, with no shell and with its home in `/var/lib/brymen-dmm`;
3. copies the application to `/opt/brymen-dmm` with an atomic folder swap, together with `uninstall.sh`;
4. installs the udev rule in `/etc/udev/rules.d/`, reloads udev and re-applies the rules only to the BC-86X hidraw
   nodes (by `HID_ID` `0003:00000820:00000001`); the other hidraw nodes on the machine receive no event;
5. installs `/etc/modules-load.d/brymen-dmm.conf`, which loads the `hid` module at boot, and loads it right away. The
   unit's `DeviceAllow=char-hidraw` is only resolved when the service starts, from the list in `/proc/devices`; on
   Debian and Ubuntu hidraw is part of the `hid` module, and without it loaded hidraw would be denied (`EPERM`) until
   the service is restarted. With HID built into the kernel (Raspberry Pi OS), nothing changes;
6. installs the unit in `/etc/systemd/system/` and creates `/etc/brymen-dmm/brymen-dmm.env` if it does not exist;
7. runs `daemon-reload`, enables and (re)starts the service. With `--no-start`, it only enables it.

### 3. Install on x64

Same as on the Raspberry Pi, with the `linux-x64` package:

```bash
sudo bash brymen-dmm-0.2.0-linux-x64/deploy/linux/install.sh brymen-dmm-0.2.0-linux-x64
```

### 4. Verify

```bash
systemctl status brymen-dmm
journalctl -u brymen-dmm -f
ls -l /dev/brymen-bu86x                          # symlink -> hidrawN
ls -lL /dev/brymen-bu86x                         # crw-rw---- root brymen-dmm
curl -s http://localhost:5080/health/ready
curl -s http://localhost:5080/api/v1/meter
```

### 5. Configure, update and remove

- **Settings**: edit `/etc/brymen-dmm/brymen-dmm.env` (for example, `Meter__Transport=Simulated` or
  `Meter__PollInterval=00:00:00.500`) and run `sudo systemctl restart brymen-dmm`. The file is not
  overwritten by updates. On a Pi with 512 MB of RAM, also set `Acquisition__MaxSamples=200000`
  ([Memory and `Acquisition:MaxSamples`](#memory-and-acquisitionmaxsamples)).
- **Update**: run `install.sh` with the new package. The data in `/var/lib/brymen-dmm` is kept.
- **Remove**: `sudo bash /opt/brymen-dmm/uninstall.sh` removes the service, the rule,
  `/etc/modules-load.d/brymen-dmm.conf` and `/opt/brymen-dmm`.
  With `--purge`, it also deletes `/var/lib/brymen-dmm`, `/etc/brymen-dmm` and the user.

How the service is set up:

- it runs as `brymen-dmm`, with `WorkingDirectory` and `DataDirectory` in `/var/lib/brymen-dmm`
  (`StateDirectory=`);
- it listens on `http://0.0.0.0:5080`, without authentication ([Security](#security)), and uses
  `Meter__DevicePath=/dev/brymen-bu86x`;
- it has `Restart=on-failure`;
- it has `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`,
  `DevicePolicy=closed` with `DeviceAllow=char-hidraw rw`, `RestrictAddressFamilies` and the like.

## Docker

The [`Dockerfile`](Dockerfile) produces a multi-architecture image (`linux/amd64` and `linux/arm64`):

- the build runs on the build machine's architecture and publishes for the target architecture with
  `dotnet publish -a $TARGETARCH`, self-contained and trimmed. The final stage has no `RUN`, so
  the cross-arch build does not need emulation (QEMU);
- the runtime image is `mcr.microsoft.com/dotnet/runtime-deps:10.0`, with the non-root user `app`;
- it exposes `5080` and has a `HEALTHCHECK` on `/health/live` and `VOLUME /data` (`DataDirectory=/data`).

```bash
# multi-architecture, pushing to a registry
docker buildx build --platform linux/amd64,linux/arm64 -t registry.example/brymen-dmm:0.2.0 --push .
# single architecture, loading into the local Docker
docker buildx build --platform linux/arm64 -t brymen-dmm:0.2.0 --load .
# Docker without the buildx plugin (legacy builder), on a 64-bit Raspberry Pi or Orange Pi
DOCKER_BUILDKIT=0 docker build --build-arg BUILDPLATFORM=linux/arm64 --build-arg TARGETARCH=arm64 -t brymen-dmm:0.2.0 .
```

The legacy builder does not set `BUILDPLATFORM` or `TARGETARCH`, which BuildKit fills in by itself: pass both with
the machine's architecture (`linux/arm64` and `arm64` on an arm64 system, `linux/amd64` and `amd64` on an x64 PC). Use it
only for the machine's own architecture; for another one, and for multi-architecture images, use `buildx`. The
`Dockerfile` declares `ARG BUILDPLATFORM` before the 1st `FROM`, with no default value: in a `FROM`, the legacy builder only
substitutes `ARG`s declared before the first `FROM`, and with BuildKit a default value would override the automatic value.

The non-root container can only open the hidraw node if the host has the udev rule, with the `brymen-dmm` group and
mode 0660, and if the container is given that group's **gid**. Install only the rule on the host
([Running on Linux](#running-on-linux), step 1).

Set `Meter__DevicePath` to the path of the node **inside** the container. It can have the same name as on the host
or a different one: a path that does not appear in the sysfs enumeration (which shows the host's names,
`/dev/hidrawN`) is opened directly, and the API checks the VID/PID through the `HIDIOCGRAWINFO` ioctl. Any other
device at that path is rejected with `E9015`.

```bash
HIDRAW=$(readlink -f /dev/brymen-bu86x)          # e.g. /dev/hidraw0
docker run -d --name brymen-dmm -p 5080:5080 --stop-timeout 45 \
  --device "$HIDRAW:$HIDRAW" \
  --group-add "$(getent group brymen-dmm | cut -d: -f3)" \
  -e Meter__DevicePath="$HIDRAW" \
  -v brymen-dmm-data:/data \
  brymen-dmm:0.2.0

# the same cable with a fixed name in the container, regardless of the host's hidrawN
docker run -d --name brymen-dmm -p 5080:5080 --stop-timeout 45 \
  --device "$(readlink -f /dev/brymen-bu86x):/dev/bu86x" \
  --group-add "$(getent group brymen-dmm | cut -d: -f3)" \
  -e Meter__DevicePath=/dev/bu86x \
  -v brymen-dmm-data:/data \
  brymen-dmm:0.2.0

# without the cable
docker run -d --name brymen-dmm-sim -p 5081:5080 -e Meter__Transport=Simulated brymen-dmm:0.2.0
```

With [`docker-compose.yml`](docker-compose.yml):

```bash
echo "BRYMEN_DMM_GID=$(getent group brymen-dmm | cut -d: -f3)" > .env
echo "BRYMEN_DMM_HIDRAW=$(readlink -f /dev/brymen-bu86x)" >> .env
docker compose up -d --build                                              # with the cable, port 5080
docker compose --profile simulated up -d --build brymen-dmm-simulated     # simulated, port 5081
```

Notes:

- the device node is created when the container starts. If the cable is replugged as a different
  `/dev/hidrawN`, update the path and recreate the container;
- Docker Desktop on Windows and macOS does not pass USB HID devices through to the container; on those
  systems use the `simulated` profile or run the API natively;
- the `HEALTHCHECK` port is fixed at 5080. If you change `Urls` in the image, adjust the Dockerfile;
- on `SIGTERM`, the SSE streams and SignalR connections end right at the start of the shutdown, but other
  in-flight requests (a large CSV export, for example) and an import still get a few seconds to finish. The
  compose file uses `stop_grace_period: 45s`; with `docker run`/`docker stop`, use `--stop-timeout 45` or `-t 45`,
  as in the examples above, so that Docker does not kill the process after 10 s.

## Security

The REST API and the SignalR hub **have no authentication or TLS**: anyone who can reach port 5080 can read the
meter, turn the link on and off, start imports and delete recordings. The service listens on `0.0.0.0` by default.

- On a trusted network, restrict the port in the host's firewall to the machine or subnet that uses the API, for
  example with `ufw`:

  ```bash
  sudo ufw allow from 192.168.0.0/24 to any port 5080 proto tcp
  sudo ufw deny 5080/tcp
  ```

  Or listen on loopback only (`Urls=http://127.0.0.1:5080`) and expose it through the proxy.
- Outside such a network, put a **reverse proxy with authentication and TLS** (nginx, Caddy, Traefik, YARP) in front
  of the API. The proxy must forward WebSockets on `/hubs/meter` (`Upgrade` and `Connection` headers) and must
  not buffer the SSE stream of `/api/v1/readings/stream` or the SignalR fallback.
- CORS (`Cors:AllowedOrigins`) is not access control: it only tells the browser which pages on other origins
  may **read** the responses. It does not prevent the request from being sent: a `POST` with no body (such as
  `/api/v1/acquisition/start`) and the hub WebSocket reach the server from any page. That is why the API rejects
  `POST`, `PUT` and `DELETE` requests and WebSocket upgrades that come from a page on another origin
  (`Sec-Fetch-Site` and `Origin` headers) with 403 (`FORBIDDEN`), unless the origin is in `Cors:AllowedOrigins`.
  Clients outside the browser (`curl`, C#), which do not send `Origin`, and the API's own dashboard get through.
  Behind a proxy that rewrites `Host`, older browsers without `Sec-Fetch-Site` need the public origin in
  `Cors:AllowedOrigins`.
- This check does not protect against **DNS rebinding**, where an outside page ends up with the same origin
  as the API. Restrict `AllowedHosts` to the names and IPs used to reach the service, for example
  `AllowedHosts=raspberrypi.local;192.168.0.10;localhost` in `/etc/brymen-dmm/brymen-dmm.env`; a `Host`
  not on the list gets 400.

## Tests

```bash
dotnet test BrymenDmm.slnx                       # everything, no hardware (uses the simulated transport)
dotnet test tests/Brymen.Dmm.Protocol.Tests      # protocol vectors and real captures
dotnet test tests/Brymen.Dmm.Hid.Tests           # BC-86X framing and HID transport
dotnet test tests/Brymen.Dmm.Tests               # Brymen.Dmm library, on net10.0 and net8.0
dotnet test tests/Brymen.Dmm.Extensions.Hosting.Tests   # AddBrymenMeter and the hosted service, on net10.0 and net8.0
dotnet test tests/Brymen.Dmm.Api.Tests           # API integration with the simulated device
```

Some tests are optional and are skipped (with the reason) when their variable is not set:

| Tests | File | Variable | Where they run |
|---|---|---|---|
| Real cable (`HardwareTests`) | `tests/Brymen.Dmm.Hid.Tests/HardwareTests.cs` and `tests/Brymen.Dmm.Tests/HardwareTests.cs` | `BRYMEN_HW_TESTS=1` | **Linux** (hidraw) and **Windows** (hid.dll) |
| Virtual cable through `/dev/uhid` (`LinuxUhidTests`) | `LinuxUhidTests.cs` | `BRYMEN_UHID_TESTS=1` | Linux, as root, with `CONFIG_UHID` and `CONFIG_HIDRAW` |

**Real cable.** Plug in the cable, turn on the meter with the cable seated in its optical port, and close the
original software and the API, which would compete for the cable. On Linux the user needs access to the hidraw node
(udev rule and the `brymen-dmm` group, as in [Running on Linux](#running-on-linux)), and one extra test,
`CableIsFoundAndOpenedByPathWithoutSysfs`, opens the cable by path without sysfs, identifying it through
`HIDIOCGRAWINFO`, as in a container with a renamed node.

```bash
BRYMEN_HW_TESTS=1 dotnet test tests/Brymen.Dmm.Hid.Tests --filter "FullyQualifiedName~HardwareTests"
```

```powershell
$env:BRYMEN_HW_TESTS = '1'; dotnet test tests/Brymen.Dmm.Hid.Tests --filter "FullyQualifiedName~HardwareTests"
```

The tests in `tests/Brymen.Dmm.Tests` open `BrymenMeter`, take a single reading, read continuously for 3 s, use the
event-driven mode and import the memory, comparing it with [`memory-dump-1.txt`](docs/protocol/captures/memory-dump-1.txt) (the meter's
memory must be the one in the capture). With the variable set, the project runs the net10.0 and net8.0 targets one
after the other; run one project at a time, because two projects running at once would compete for the cable:

```powershell
$env:BRYMEN_HW_TESTS = '1'; dotnet test tests/Brymen.Dmm.Tests --filter "FullyQualifiedName~HardwareTests"
```

The hardware tests only use the documented read commands (`52 66`, `82 66`, `52 88`, `52 89` and
`52 8A`); reading does not change the meter's memory. They work with both meters: each test detects the family
through `82 66` (Model ID `52h` or `82h`) and resends an unanswered request, like the library. With the BM525s, the
import is compared with the memory in the capture. With the ET-2517A, which has no datalogger, it must fail with
`E9014` without any `88`/`89`/`8A`, and `52 66` must go unanswered. There is also a 10 s test at 500 ms that checks
that a recoverable loss does not reopen the cable. The regular tests (no variable) cover both families with the
simulated meters, including the ET-2517A with the measured request dropping.

**Virtual cable (uhid).** Without the cable, the tests create a virtual BC-86X through the kernel's `/dev/uhid`
and exercise the real hidraw driver: libc `open`/`poll`/`read`/`write`, reports without a report ID, timeouts and
hot removal. `/dev/uhid` and the hidraw node it creates belong to root, so run with `sudo -E`. Build first
as your own user, so that `sudo` does not leave root-owned files in `bin/` and `obj/`:

```bash
dotnet build tests/Brymen.Dmm.Hid.Tests
sudo -E BRYMEN_UHID_TESTS=1 dotnet test tests/Brymen.Dmm.Hid.Tests --no-build --filter "FullyQualifiedName~LinuxUhidTests"
```

If `sudo` cannot find `dotnet` (`secure_path` ignores the user's `PATH`), use
`sudo -E env "PATH=$PATH" BRYMEN_UHID_TESTS=1 dotnet test ...`.

## Troubleshooting

### Permission denied on `/dev/hidrawN` (EACCES)

- Check the rule and the owner: `ls -lL /dev/brymen-bu86x` (`-L` follows the symlink to the hidraw node) should
  show `crw-rw---- root brymen-dmm`, with a `+` at the end of the mode when `uaccess` has granted an ACL to a
  local session user. If the symlink does not exist, the rule did not match. If the hidraw node shows
  `root root`, the group did not exist when the rule ran:
  create it (`sudo groupadd --system brymen-dmm`) and re-apply the rule to the cable's hidraw, with the loop from
  step 1 of [Running on Linux](#running-on-linux) or by unplugging and replugging the cable.
- Check whether the rule matched: `udevadm info --name=/dev/hidraw0` (look for `DEVLINKS=/dev/brymen-bu86x` and
  `TAGS=:uaccess:`) and `udevadm test "$(udevadm info --query=path --name=/dev/hidraw0)"`.
- Outside the local session (SSH, cron), `uaccess` does not apply: the user must be in the
  `brymen-dmm` group (`id` shows the groups; after `usermod -aG`, open a new session).
- In the service, `DeviceAllow=char-hidraw` requires hidraw to be listed in `/proc/devices` (`grep hidraw /proc/devices`)
  **when the service starts**: the list is only resolved at startup. An `EPERM` (rather than `EACCES`) when opening the
  device points to this case. Check `/etc/modules-load.d/brymen-dmm.conf` (installed by `install.sh`) and run
  `sudo systemctl restart brymen-dmm`.

### E9015 "Cabo adaptador USB não encontrado" (HTTP 503)

- The cable is the USB device; the multimeter is not. Check the enumeration:
  - Linux: `lsusb | grep -i 0820` and `grep -H HID_ID /sys/class/hidraw/*/device/uevent`, which should
    show `HID_ID=0003:00000820:00000001`;
  - Windows: Device Manager, "HID-compliant device" with `VID_0820&PID_0001`.
- If `Meter:DevicePath` is set, check the path (in the service it is `/dev/brymen-bu86x`, which only
  exists with the cable plugged in and the rule installed).
- In Docker, check `devices:` and that `Meter__DevicePath` is the path of the node **in the container**. A node that
  is not the BC-86X (VID/PID read through `HIDIOCGRAWINFO`) is rejected. Without permission on the node (wrong gid in
  `--group-add`), the `E9015` carries the open error in `detail` (and in `lastError.message` of `GET /api/v1/meter`),
  with a hint about the udev rule; it also appears once in the container log.
- Close the original software or any other instance of the API, since they compete for the cable.

### Meter turned off or in APO

The cable stays enumerated with the meter turned off, but nothing responds:

- for real-time data, the result is `W7012` (no data received; on the BM82x and during family detection, after 2 s of
  resends), and the service retries every `Meter:ReconnectDelay`. After 3 consecutive queries without a response, the
  family becomes unknown again (`model: null`) and is detected again when the meter responds;
- during an import, the result is `E9016` (timeout) or `E9017` (meter turned off or without a
  datalogger).

Turn the meter on, seat the cable in the optical port and, for long recordings, disable auto power off
(APO) as described in the meter's manual. What the cable returns with the meter turned off has not been
characterized yet, on either the BM525s or the ET-2517A ([questions 9 and 17](#limitations-and-open-questions)).

### Many resends or `W7012` with the ET-2517A

The ET-2517A drops real-time requests, and resends are normal ([Family and dropped requests](#family-and-dropped-requests)):
they show up in `realTimeStatistics.resends` of `GET /api/v1/meter`, not as errors. If `W7012` still occurs:

- lower `Meter:PollInterval` (for example, `00:00:00.100`): the shorter the interval, the fewer requests are lost (at
  0 ms, none out of 40);
- raise `Meter:RealTimeResendBudget` (for example, `00:00:03`), for more resends per query;
- check that the cable is seated in the optical port and that the meter has not entered APO.

### Linux without `/dev/hidraw*`

- The kernel needs `CONFIG_HIDRAW`: `grep HIDRAW /boot/config-$(uname -r)` or
  `zgrep HIDRAW /proc/config.gz`. Debian, Ubuntu and Raspberry Pi OS already include it.
- The USB HID driver must be loaded: `lsmod | grep -E 'usbhid|hid_generic'`. If it is missing,
  run `sudo modprobe -a usbhid hid_generic` (without `-a`, the second name becomes a parameter of the first).
- Check the messages when plugging in the cable: `sudo dmesg -w` should show a line with `hidrawN` and
  `Brymen Superior DMM`.
- On WSL2 the USB device must be attached with `usbipd-win`. Otherwise, use the simulated mode.

### The service does not start

- `journalctl -u brymen-dmm -e`. `Exec format error` indicates a package for the wrong architecture. `status=203/EXEC`
  with `No such file or directory` for an executable that exists indicates a 32-bit userland (Raspberry Pi OS
  armhf with an arm64 kernel): the `/lib/ld-linux-aarch64.so.1` interpreter does not exist. Install the 64-bit
  OS.
- With `Type=notify`, systemd waits up to 60 s for `READY=1`. If the service stays in `activating`
  and fails with a timeout, check whether a drop-in (`systemctl cat brymen-dmm`) sets `ProtectProc=`.
  `UseSystemd` only detects systemd by reading `/proc/<ppid>/comm`, and that setting hides or blocks
  `/proc/1`.
- Port already in use: change `Urls` in `/etc/brymen-dmm/brymen-dmm.env`.

## Limitations and open questions

Open questions about the protocol ([§6 of the specification](docs/protocol/bm52x-usb-protocol.md#6-questões-em-aberto)):

1. The exact LCD pattern on overload (`0.L`, ` 0L `, position of the decimal point) and whether the sign lights up on OL.
   Captures in Ω with open test leads and in V above the range are missing.
2. Temperature: decimal places in the memory and the real-time format. Captures in °C/°F are missing.
3. The meaning of `b18`, of the low nibble of `b24` and of the @, [H], [C] and [R] symbols.
4. Why the original rejects frames with @ lit and which values it accepts in byte 23.
5. The meaning of Model_Id = 00 × 01.
6. The address mapping for pages A2/A3, with more than 64 KiB of log.
7. The actual chaining with several sessions and with a 3-byte single display: the real dump has only one
   dual-display session.
8. The hidraw behavior (8 bytes per `read()`, `write()` with report ID 0) still needs to be
   confirmed with the real cable on arm64 and x64. The captures are from Windows; the `HardwareTests`
   (real cable) and `LinuxUhidTests` (virtual cable) tests are meant for this check ([Tests](#tests)).
9. Whether the cable responds, and with what, when the meter is turned off or out of communication mode.
10. Whether the memory pointer advances on an `89` whose response was lost. This determines whether recovery
    with `8A` is safe.
11. Why the first `66` after the last memory block takes about 2 s and comes back with dashes, and whether the
    delay grows with larger memories. The port's 3 s wait covers the measured 2272-byte dump.
12. Why the ET-2517A drops real-time requests, and why the rate grows with the interval between queries.
13. Whether, with the 2 s budget, any request is still left unanswered. With 4 attempts (about 1.2 s), 1 to 5 out of
    40 queries failed; the 2 s budget has not been measured on the hardware.
14. What the BM525s does with a resent `66` while it is still preparing the slow response to the 1st `66` after the
    memory. That is why the port does not resend during this wait.
15. What the ET-2517A's LCD shows when the secondary display comes with only the decimal point, in a manual ACV range.
16. Whether any BM52x ignores `82 66` (the detection's fallback `52 66` covers that case) and whether other BM82x
    meters return other values in the Model ID.
17. Whether the ET-2517A answers `82 66` in APO, in setup mode or with the selector at OFF.

Captures of the ET-2517A's EF mode (electric field detection) are also missing: the byte map has no bit for it, and
it is not known whether `E.F.` and the intensity dashes arrive in the digits. The bar graph is not transmitted.

From the original software ([§14 of the RE report](docs/re/bs8252x-reverse-engineering.md#14-questões-em-aberto)):

- the original polls the real-time data with `82 66` on both families. The port uses `82 66` during detection and on
  the BM82x, and `52 66` on the BM525s after detection, the command validated on the hardware; on the BM525s both
  return identical frames ([`cmd-sequences-2026-10-22.txt`](docs/protocol/captures/cmd-sequences-2026-10-22.txt), §2.7
  and §8.1 of the specification);
- the `.slr` format, the legacy `.g51`/`.g52` layouts and the exact multiplier table of the Analog Meter
  were not analyzed.

Limitations of the port:

- only commands `66` (with `52` or `82`), `88`, `89` and `8A` (with `52`) are sent;
- the family comes from the Model ID, which does not distinguish the models within a family (an ET-2517A from a
  BM829s, for example);
- the continuous backup is CSV, not the binary `~BDMDLn.TMP`; there is no reading or writing of `.g53`, and no
  import of `BTCDL.ini`;
- the web dashboard is minimal and does not reproduce the interface of the original (ProEssentials charts,
  analog meter and comparator display);
- the comparator alarm is exposed as state in the API; the server does not beep;
- there is no support for the serial port (`iPort`) or for the BM860s series (a different protocol, on the same
  BU-86X kit);
- the API and the SignalR hub have no authentication or TLS. Expose them only on a trusted network, with a firewall, or
  behind a reverse proxy with authentication ([Security](#security)).

## Disclaimer

Brymen is a trademark of Brymen Technology Corporation, and Minipa is a trademark of Minipa. BM525s, BM82x, BC-86X,
BU-86X, ET-2615A and ET-2517A are product designations of these companies, and Greenlee is a trademark of its owner;
the models cited as possibly compatible have not been tested. The *Bs82-52x Data Recording System* software and
the protocol documents cited belong to Brymen:

- `10000count-professional-dual-display-mobile-logging-DMMs-protocol.pdf`;
- `10000count-professional-dual-display-DMMs-protocol.pdf`;
- `Memory-Read-Allocation-Decoder.pdf`.

These documents and the software are used only as a reference and are not redistributed by this
repository.

This project is independent and is not affiliated with, endorsed by or supported by Brymen or Minipa. The
reverse engineering was static and its sole purpose was interoperability with the hardware.
The software is provided "as is", without warranty of any kind. Use it at your own risk,
especially in measurements that involve electrical safety.

## License

[MIT](LICENSE), for the code in this repository. The Brymen documents and software cited above are not covered by
it.
