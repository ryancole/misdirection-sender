#Requires -Version 7
<#
.SYNOPSIS
  Run the test suite.
.EXAMPLE
  etc\test.ps1
  etc\test.ps1 -Filter OptionsParser     # only tests whose name contains OptionsParser
#>
[CmdletBinding()]
param(
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testArgs = @((Join-Path $root 'src' 'Misdirection.Sender.Tests' 'Misdirection.Sender.Tests.csproj'))
if ($Filter) { $testArgs += '--filter', "FullyQualifiedName~$Filter" }

dotnet test @testArgs
exit $LASTEXITCODE
