<#
.SYNOPSIS
    Publishes SharpPXE.exe (Native AOT) and SharpPXE.Gui.exe into one folder per architecture.

.EXAMPLE
    .\build.ps1                 # x64 -> publish\win-x64
    .\build.ps1 -Rid win-arm64  # ARM64 -> publish\win-arm64
#>
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "publish\$Rid"

# Native AOT's linker setup calls vswhere.exe, which isn't always on PATH.
$installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
if ((Test-Path $installer) -and -not ($env:PATH -split ';' -contains $installer)) { $env:PATH = "$installer;$env:PATH" }

# Server: one self-contained native exe, no .NET runtime needed.
dotnet publish "$PSScriptRoot\src\SharpPXE\SharpPXE.csproj" -c $Configuration -r $Rid -o $out
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# GUI: WinForms can't use Native AOT; a single-file exe that uses the installed .NET 10 Desktop Runtime.
dotnet publish "$PSScriptRoot\src\SharpPXE.Gui\SharpPXE.Gui.csproj" -c $Configuration -r $Rid -o $out `
    --self-contained false -p:PublishSingleFile=true
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# Debug symbols and leftover files aren't needed to run.
Get-ChildItem $out -Filter *.pdb | Remove-Item
Get-ChildItem $out -Filter *.staticwebassets.endpoints.json | Remove-Item

Write-Host "`nPublished to $out" -ForegroundColor Green
Get-ChildItem $out -Recurse -File | Select-Object @{n='File';e={$_.FullName.Substring($out.Length + 1)}}, @{n='KB';e={[math]::Round($_.Length / 1KB)}} | Format-Table -AutoSize
