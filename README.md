# misdirection-sender

Command-line tool that replays a `.msdr` protocol file to a
[misdirection](../misdirection) device over serial, using
[misdirection-client](submodules/misdirection-client).

## Setup

The client library is a git submodule:

```bash
git submodule update --init
```

## Usage

```
misdirection-sender <file.msdr> --port <name> [options]
misdirection-sender <file.msdr> --dry-run
misdirection-sender --list-ports
```

| Option | |
|---|---|
| `-p, --port <name>` | Serial port the device is on, e.g. `COM5` |
| `-b, --baud <rate>` | Baud rate (default 115200) |
| `-d, --delay <ms>` | Pause between messages (default 0) |
| `-s, --screen <WxH>` | Send `SCREEN_SIZE` before the file's messages |
| `--continue-on-nack` | Keep going after a NACK instead of stopping |
| `--no-ping` | Skip the PING handshake and the confirming PING at the end |
| `-n, --dry-run` | Validate the file and list its messages; no port is opened |
| `-v, --verbose` | Print each message as it is sent |
| `--list-ports` | List serial ports |

What a run does:

1. Reads and validates the whole file first. A malformed file sends nothing.
2. PINGs the device and checks it speaks the same protocol version.
3. Sends `SCREEN_SIZE` if `--screen` was given, then every message in order, pausing
   `--delay` ms after each. Device-to-host messages in the file (PONG/NACK) are skipped.
4. PINGs again. The firmware handles frames in order, so the PONG confirms every message
   was processed and any NACK they caused has arrived.

On a NACK, or on Ctrl+C, it stops and sends `PANIC` so no key or button is left held.
NACKs arrive asynchronously, so the report gives how many messages had been sent when one
arrived; the message that caused it is at or before that position.

`.msdr` files carry no timing, so `--delay` is the only pacing. Windows timer resolution
rounds small delays up to about 15 ms.

Exit codes: `0` sent, `1` error, `2` bad arguments, `3` device NACKed, `130` cancelled.

## Layout

```
src/
  MisdirectionSender.slnx      includes the client project from the submodule
  Misdirection.Sender/         the console app (assembly misdirection-sender)
    Program.cs                 entry point, Ctrl+C handling
    Cli.cs                     load file, open port, handshake, report
    SenderOptions.cs           options record and command-line parser
    MessageSender.cs           send loop: pacing, NACK watch, confirm PING, PANIC on abort
  Misdirection.Sender.Tests/   xunit; FakeDevice stands in for the firmware
submodules/
  misdirection-client/         protocol library
etc/
  build.ps1                    dotnet build
  test.ps1                     dotnet test [-Filter name]
  run.ps1                      dotnet run, arguments passed through
  publish.ps1                  self-contained single-file exe into artifacts/<rid>
```
