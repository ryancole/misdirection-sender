#Requires -Version 7
<#
.SYNOPSIS
  Move the mouse in a circle on the target, to check the device works end to end.
.DESCRIPTION
  Generates a .msdr file of mouse moves around a circle (etc\circle.cs) and plays it with the
  sender, so it goes through the same path a recording does: PING handshake, SCREEN_SIZE, timed
  moves, confirming PING.

  Absolute mode (the default) centres the circle on the screen and needs the target's real
  resolution in -Screen, or the circle lands scaled or off-centre. -Relative traces the circle
  from wherever the pointer is with MOUSE_MOVE_REL, which doesn't depend on the screen size;
  the target's pointer speed and acceleration will distort its shape. Trying both tells a
  screen-size problem apart from a dead link.

  -Verbose prints each message as it is sent. Any other sender option can follow the script's
  own, without a '--' separator (pwsh -File rejects that).
.EXAMPLE
  etc\circle.ps1 -Port COM5
  etc\circle.ps1 -Port COM5 -Screen 2560x1440 -Radius 400 -Loops 5
  etc\circle.ps1 -Port COM5 -Relative -Radius 100
  etc\circle.ps1 -Port COM5 -Verbose --delay 5
  etc\circle.ps1 -DryRun                     # list the messages; no port opened
#>
[CmdletBinding()]
param(
    [string]$Port,
    [string]$Screen = '1920x1080',
    [int]$Radius = 300,
    [int]$Loops = 3,
    [double]$SecondsPerLoop = 2,
    [switch]$Relative,
    [switch]$DryRun,
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$SenderArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Port -and -not $DryRun) { throw 'Give -Port (e.g. COM5), or -DryRun. etc\run.ps1 --list-ports lists them.' }
if ($Screen -notmatch '^(\d+)x(\d+)$') { throw "-Screen must be WIDTHxHEIGHT, e.g. 1920x1080, not '$Screen'." }
$width, $height = [int]$Matches[1], [int]$Matches[2]

$file = Join-Path ([IO.Path]::GetTempPath()) 'misdirection-circle.msdr'
$mode = $Relative ? 'rel' : 'abs'
$inv = [Globalization.CultureInfo]::InvariantCulture

dotnet run (Join-Path $PSScriptRoot 'circle.cs') -- $file $width $height $Radius $Loops $SecondsPerLoop.ToString($inv) $mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$sendArgs = @($file)
$sendArgs += $DryRun ? '--dry-run' : @('--port', $Port)
# SCREEN_SIZE first so absolute moves scale to the target; harmless for relative ones.
$sendArgs += '--screen', "${width}x${height}"
if ($PSBoundParameters['Verbose']) { $sendArgs += '--verbose' }
if ($SenderArgs) { $sendArgs += $SenderArgs }

& (Join-Path $PSScriptRoot 'run.ps1') @sendArgs
exit $LASTEXITCODE
