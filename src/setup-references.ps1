# Setup script to copy Unity and BepInEx DLLs for development
param(
    [Parameter(Mandatory=$true)]
    [string]$UnityPath
)

Write-Host "Setting up development references..." -ForegroundColor Cyan

# Validate Unity path
if (-not (Test-Path $UnityPath)) {
    Write-Host "Error: Unity path not found: $UnityPath" -ForegroundColor Red
    exit 1
}

# Auto-detect the *_Data/Managed folder (every Unity game should name it like <GameName>_Data)
$dataFolder = Get-ChildItem -Path $UnityPath -Directory -Filter "*_Data" |
    Where-Object { Test-Path (Join-Path $_.FullName "Managed") } |
    Select-Object -First 1

if (-not $dataFolder) {
    Write-Host "Error: No *_Data\Managed folder found in $UnityPath" -ForegroundColor Red
    exit 1
}

$managedDir = Join-Path $dataFolder.FullName "Managed"
Write-Host "Found Unity data folder: $($dataFolder.Name)\Managed" -ForegroundColor Cyan

# Auto-detect BepInEx core folder
$bepInExCore = Join-Path $UnityPath "BepInEx\core"
if (-not (Test-Path $bepInExCore)) {
    Write-Host "Error: BepInEx not found at $bepInExCore" -ForegroundColor Red
    Write-Host "Install BepInEx from: https://github.com/BepInEx/BepInEx/releases" -ForegroundColor Yellow
    exit 1
}

# Create lib directory
$libDir = "lib"
if (-not (Test-Path $libDir)) {
    New-Item -ItemType Directory -Path $libDir | Out-Null
}

# DLLs to copy: name -> resolved source path
$references = @{
    "BepInEx.dll"                = Join-Path $bepInExCore "BepInEx.dll"
    "0Harmony.dll"               = Join-Path $bepInExCore "0Harmony.dll"
    "UnityEngine.dll"            = Join-Path $managedDir  "UnityEngine.dll"
    "UnityEngine.CoreModule.dll" = Join-Path $managedDir  "UnityEngine.CoreModule.dll"
}

$success = $true

foreach ($dll in $references.Keys) {
    $sourcePath = $references[$dll]
    $destPath = Join-Path $libDir $dll
    
    if (Test-Path $sourcePath) {
        Write-Host "Copying $dll..." -ForegroundColor Green
        Copy-Item $sourcePath $destPath -Force
    }
    else {
        Write-Host "Warning: $dll not found at $sourcePath" -ForegroundColor Yellow
        $success = $false
    }
}

if ($success) {
    Write-Host "`nSetup complete! You can now build the project." -ForegroundColor Green
    Write-Host "Run: dotnet build" -ForegroundColor Cyan
}
else {
    Write-Host "`nSetup incomplete. Make sure BepInEx is installed in Unity." -ForegroundColor Yellow
    Write-Host "Install BepInEx from: https://github.com/BepInEx/BepInEx/releases" -ForegroundColor Yellow
}
