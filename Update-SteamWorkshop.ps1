param(
    [Parameter(Mandatory = $true)]
    [string]$UploaderDir,

    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$modRoot = Join-Path $root "mods\neowlogs-sts2"
$workshopWorkspace = Join-Path $root "steam-workshop\NeowLogs"
$uploaderExe = Join-Path $UploaderDir "ModUploader.exe"

if (-not (Test-Path -LiteralPath $uploaderExe)) {
    throw "ModUploader.exe was not found in '$UploaderDir'. Pass the folder where you extracted ModUploader-win-x64.zip."
}

if (-not (Test-Path -LiteralPath (Join-Path $workshopWorkspace "workshop.json"))) {
    throw "Workshop workspace was not found at '$workshopWorkspace'."
}

if (-not $SkipBuild) {
    Push-Location $modRoot
    try {
        dotnet build -c $Configuration
    } finally {
        Pop-Location
    }
}

$packageScript = Join-Path $modRoot "Package-SteamWorkshop.ps1"
& $packageScript -WorkshopWorkspace $workshopWorkspace -Configuration $Configuration -IncludeSymbols:$IncludeSymbols

Push-Location $UploaderDir
try {
    & $uploaderExe upload -w $workshopWorkspace
} finally {
    Pop-Location
}
