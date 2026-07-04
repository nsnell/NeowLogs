param(
    [string]$Sts2InstallDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Sts2InstallDir)) {
    $candidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
        "C:\Program Files\Steam\steamapps\common\Slay the Spire 2"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            $Sts2InstallDir = $candidate
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Sts2InstallDir) -or -not (Test-Path -LiteralPath $Sts2InstallDir)) {
    throw "Could not find Slay the Spire 2. Re-run with: .\Install-NeowLogs.ps1 -Sts2InstallDir '<path to Slay the Spire 2>'"
}

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $projectRoot ".godot\mono\temp\bin\Debug"
$dll = Join-Path $buildDir "NeowLogs.dll"
$pdb = Join-Path $buildDir "NeowLogs.pdb"
$manifest = Join-Path $projectRoot "NeowLogs.json"

if (-not (Test-Path -LiteralPath $dll)) {
    throw "NeowLogs.dll was not found at $dll. Build the project first, then run this installer again."
}
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "NeowLogs.json was not found at $manifest."
}

$modsDir = Join-Path $Sts2InstallDir "mods"
$targetDir = Join-Path $modsDir "NeowLogs"

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $targetDir "NeowLogs.dll") -Force
Copy-Item -LiteralPath $manifest -Destination (Join-Path $targetDir "NeowLogs.json") -Force
if (Test-Path -LiteralPath $pdb) {
    Copy-Item -LiteralPath $pdb -Destination (Join-Path $targetDir "NeowLogs.pdb") -Force
}

Write-Host "Installed NeowLogs to:"
Write-Host $targetDir
Write-Host ""
Write-Host "Folder contents:"
Get-ChildItem -LiteralPath $targetDir | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize
