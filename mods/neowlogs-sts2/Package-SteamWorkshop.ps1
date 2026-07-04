param(
    [string]$WorkshopWorkspace = "..\..\steam-workshop\NeowLogs",
    [string]$Configuration = "Debug",
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspacePath = if ([System.IO.Path]::IsPathRooted($WorkshopWorkspace)) {
    $WorkshopWorkspace
} else {
    Join-Path $projectRoot $WorkshopWorkspace
}
$contentDir = Join-Path $workspacePath "content"
$buildDir = Join-Path $projectRoot ".godot\mono\temp\bin\$Configuration"
$dll = Join-Path $buildDir "NeowLogs.dll"
$pdb = Join-Path $buildDir "NeowLogs.pdb"
$manifest = Join-Path $projectRoot "NeowLogs.json"

if (-not (Test-Path -LiteralPath $dll)) {
    throw "NeowLogs.dll was not found at $dll. Build the mod first, then run this script again."
}
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "NeowLogs.json was not found at $manifest."
}

New-Item -ItemType Directory -Force -Path $contentDir | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $contentDir "NeowLogs.dll") -Force
Copy-Item -LiteralPath $manifest -Destination (Join-Path $contentDir "NeowLogs.json") -Force

if ($IncludeSymbols) {
    if (-not (Test-Path -LiteralPath $pdb)) {
        throw "NeowLogs.pdb was not found at $pdb."
    }
    Copy-Item -LiteralPath $pdb -Destination (Join-Path $contentDir "NeowLogs.pdb") -Force
} else {
    $stagedPdb = Join-Path $contentDir "NeowLogs.pdb"
    if (Test-Path -LiteralPath $stagedPdb) {
        Remove-Item -LiteralPath $stagedPdb -Force
    }
}

Write-Host "Steam Workshop content staged at:"
Write-Host $contentDir
Write-Host ""
Get-ChildItem -LiteralPath $contentDir | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize
