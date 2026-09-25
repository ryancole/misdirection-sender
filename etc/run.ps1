#Requires -Version 7
<#
.SYNOPSIS
  Build and run the sender, passing every argument through.
.EXAMPLE
  etc\run.ps1 drag.msdr --port COM5
  etc\run.ps1 drag.msdr --dry-run
  etc\run.ps1 --list-ports
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet run --project (Join-Path $root 'src' 'Misdirection.Sender' 'Misdirection.Sender.csproj') -- @args
exit $LASTEXITCODE
