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
| `-x, --speed <factor>` | Playback speed: `2` is twice as fast, `0.5` half speed (default 1) |
| `--ignore-timing` | Ignore the file's delays and send as fast as `--delay` allows |
| `-d, --delay <ms>` | Minimum time between messages (default 0) |
| `-s, --screen <WxH>` | Send `SCREEN_SIZE` before the file's messages |
| `--continue-on-nack` | Keep going after a NACK instead of stopping |
| `--no-ping` | Skip the PING handshake and the confirming PING at the end |
| `-n, --dry-run` | Validate the file and list its messages; no port is opened |
| `-v, --verbose` | Print each message as it is sent |
| `--list-ports` | List serial ports |

What a run does:

1. Reads and validates the whole file first. A malformed file sends nothing. The file is
   opened read-only with write sharing, so one a recorder still has open for appending
   plays as it stood when the sender opened it.
2. PINGs the device and checks it speaks the same protocol version.
3. Sends `SCREEN_SIZE` if `--screen` was given, then every message in order at the
   pace recorded in the file (see Timing). Device-to-host messages in the file
   (PONG/NACK) are skipped.
4. PINGs again. The firmware handles frames in order, so the PONG confirms every message
   was processed and any NACK they caused has arrived.

On a NACK, or on Ctrl+C, it stops and sends `PANIC` so no key or button is left held.
NACKs arrive asynchronously, so the report gives how many messages had been sent when one
arrived; the message that caused it is at or before that position.

Exit codes: `0` sent, `1` error, `2` bad arguments, `3` device NACKed, `130` cancelled.

## Timing

A `.msdr` file can carry `FILE_DELAY` records, the time between one message and the
next. The sender never puts them on the wire; it reads the file with
`ProtocolFile.ReadTimed`, which gives each message its offset from the start, and
sends each one when a single playback clock reaches `offset / speed`. Waiting on one
clock, rather than sleeping per gap, means an oversleep on one message doesn't delay
the rest.

`Task.Delay` can oversleep by a full Windows timer tick (~15.6 ms), so the sender
sleeps until 20 ms before a message is due and spins for the rest. Messages go out
within a fraction of a millisecond of their scheduled time, at the cost of a busy CPU
core during the last 20 ms before each one.

`--delay` sets a floor between consecutive messages on top of the file's timing: a
message is sent at its scheduled time or `--delay` ms after the previous one,
whichever is later. With `--ignore-timing` it's the only pacing. Files without delay
records are sent as fast as possible.

## Layout

```
src/
  MisdirectionSender.slnx      includes the client project from the submodule
  Misdirection.Sender/         the console app (assembly misdirection-sender)
    Program.cs                 entry point, Ctrl+C handling
    Cli.cs                     load file, open port, handshake, report
    SenderOptions.cs           options record and command-line parser
    MessageSender.cs           send loop: scheduling, NACK watch, confirm PING, PANIC on abort
    PlaybackClock.cs           single-clock waits: sleep, then spin the last 20 ms
  Misdirection.Sender.Tests/   xunit; FakeDevice stands in for the firmware
submodules/
  misdirection-client/         protocol library
etc/
  build.ps1                    dotnet build
  test.ps1                     dotnet test [-Filter name]
  run.ps1                      dotnet run, arguments passed through
  publish.ps1                  self-contained single-file exe into artifacts/<rid>
```
