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

# Create lib directory
$libDir = "lib"
if (-not (Test-Path $libDir)) {
    New-Item -ItemType Directory -Path $libDir | Out-Null
}

# Define required DLLs and their locations
$references = @{
    "BepInEx.dll" = "BepInEx\core\BepInEx.dll"
    "0Harmony.dll" = "BepInEx\core\0Harmony.dll"
    "UnityEngine.dll" = "Unity_Data\Managed\UnityEngine.dll"
    "UnityEngine.CoreModule.dll" = "Unity_Data\Managed\UnityEngine.CoreModule.dll"
}

$success = $true

foreach ($dll in $references.Keys) {
    $sourcePath = Join-Path $UnityPath $references[$dll]
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
