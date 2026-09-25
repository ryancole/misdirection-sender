#Requires -Version 7
<#
.SYNOPSIS
  Publish a self-contained single-file misdirection-sender executable.
.EXAMPLE
  etc\publish.ps1                          # win-x64 into artifacts\win-x64
  etc\publish.ps1 -Runtime linux-x64
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts' $Runtime

dotnet publish (Join-Path $root 'src' 'Misdirection.Sender' 'Misdirection.Sender.csproj') `
    -c Release -r $Runtime --self-contained -p:PublishSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Published to $out"
