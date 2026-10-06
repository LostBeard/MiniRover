<#
.SYNOPSIS
  Deploys the car application (firmware\MiniRover.Car) by writing its deployment image straight to flash.

.DESCRIPTION
  The nanoFramework build produces MiniRover.Car.bin: every assembly of the app, laid out as the CLR expects in
  the "deploy" partition (0x1E0000 on the MINIROVER_ESP32 target). Writing it with esptool takes a few seconds
  and does not need the nanoFramework debugger (a debugger deploy takes minutes over the kit's CH340 at 460800).
  The car restarts afterwards. Settings (pairing key, calibration) and WiFi credentials live in other
  partitions and are not touched.

  The firmware (nanoCLR) must match the app: if an interop assembly changed, flash the new firmware first
  (Docs/firmware-build.md), or the CLR refuses to run the app.

.EXAMPLE
  .\firmware\deploy-app.ps1 -Port COM8
#>
param(
    [Parameter(Mandatory = $true)][string]$Port,
    [string]$Image = '',
    [string]$Address = '0x1e0000',
    [int]$Baud = 460800
)
$ErrorActionPreference = 'Stop'
# Resolved here, not in the param default: Windows PowerShell 5.1 leaves $PSScriptRoot empty there.
if (-not $Image) { $Image = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'MiniRover.Car\bin\Debug\MiniRover.Car.bin' }

if (-not (Test-Path $Image)) { throw "No deployment image at $Image. Build firmware\MiniRover.Car first." }

# esptool: from PATH, else the copy that ships with nanoff (dotnet tool install -g nanoff).
$esptool = (Get-Command esptool.exe -ErrorAction SilentlyContinue).Source
if (-not $esptool) {
    $esptool = Get-ChildItem "$env:USERPROFILE\.dotnet\tools\.store\nanoff" -Recurse -Filter esptool.exe -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $esptool) { throw "esptool.exe not found. Install nanoff (dotnet tool install -g nanoff) or put esptool on PATH." }

$size = (Get-Item $Image).Length
Write-Host "Writing $Image ($size bytes) to $Address on $Port with $esptool"
& $esptool --chip esp32 --port $Port --baud $Baud --before default-reset --after hard-reset write-flash $Address $Image
if ($LASTEXITCODE -ne 0) { throw "esptool failed ($LASTEXITCODE)" }
Write-Host "Done: the car restarts and runs the new app."
