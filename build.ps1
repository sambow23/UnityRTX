# Build script for Unity Remix plugin
param(
    [string]$UnityPath = "",
    [switch]$Deploy
)

Write-Host "Building Unity RTX Remix Plugin..." -ForegroundColor Cyan

# Get git hash
$gitHash = "unknown"
try {
    $gitHash = git rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Git hash: $gitHash" -ForegroundColor Cyan
    }
} catch {
    Write-Host "Warning: Could not get git hash" -ForegroundColor Yellow
}

# Generate version info file
$versionContent = @"
// Auto-generated file - do not edit manually
namespace UnityRemix
{
    public static class BuildInfo
    {
        public const string GitHash = "$gitHash";
    }
}
"@
Set-Content -Path "BuildInfo.cs" -Value $versionContent -Encoding UTF8
Write-Host "Generated BuildInfo.cs" -ForegroundColor Green

# Build the project
Write-Host "Running dotnet build..." -ForegroundColor Yellow
dotnet build --configuration Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green

# Deploy if requested
if ($Deploy -and $UnityPath -ne "") {
    $pluginPath = Join-Path $UnityPath "BepInEx\plugins"
    $dllSource = "bin\Release\netstandard2.1\UnityRemix.dll"
    
    if (-not (Test-Path $pluginPath)) {
        Write-Host "BepInEx plugins folder not found at: $pluginPath" -ForegroundColor Red
        Write-Host "Make sure BepInEx is installed and path is correct" -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host "Deploying to: $pluginPath" -ForegroundColor Yellow
    Copy-Item $dllSource $pluginPath -Force
    Write-Host "Plugin deployed successfully!" -ForegroundColor Green
    
    # Check for Remix DLL
    $remixDll = Join-Path $UnityPath "d3d9.dll"
    if (-not (Test-Path $remixDll)) {
        Write-Host "`nWARNING: d3d9.dll (RTX Remix) not found in game folder!" -ForegroundColor Yellow
        Write-Host "Please install RTX Remix runtime to: $UnityPath" -ForegroundColor Yellow
    } else {
        Write-Host "RTX Remix DLL found!" -ForegroundColor Green
    }
}
elseif ($Deploy) {
    Write-Host "`nTo deploy, run: .\build.ps1 -Deploy -UnityPath 'C:\Path\To\Unity'" -ForegroundColor Yellow
}
else {
    Write-Host "`nPlugin built at: bin\Release\netstandard2.1\UnityRemix.dll" -ForegroundColor Cyan
    Write-Host "To build and deploy: .\build.ps1 -Deploy -UnityPath 'C:\Path\To\Unity'" -ForegroundColor Yellow
}
