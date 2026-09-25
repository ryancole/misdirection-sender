#Requires -Version 7
<#
.SYNOPSIS
  Build the solution.
.EXAMPLE
  etc\build.ps1              # Debug
  etc\build.ps1 -Release
#>
[CmdletBinding()]
param(
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$config = $Release ? 'Release' : 'Debug'

dotnet build (Join-Path $root 'src' 'MisdirectionSender.slnx') -c $config
exit $LASTEXITCODE
