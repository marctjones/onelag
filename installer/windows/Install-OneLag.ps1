param(
    [string] $InstallDir = "$env:LOCALAPPDATA\Programs\OneLag",
    [switch] $NoPath
)

$ErrorActionPreference = "Stop"

$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $sourceDir "onelag.exe"

if (-not (Test-Path $exePath)) {
    throw "onelag.exe was not found next to Install-OneLag.ps1. Extract the release zip before installing."
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $sourceDir "*") -Destination $InstallDir -Recurse -Force

if (-not $NoPath) {
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $parts = @()
    if (-not [string]::IsNullOrWhiteSpace($userPath)) {
        $parts = $userPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }

    if ($parts -notcontains $InstallDir) {
        $newPath = (@($parts) + $InstallDir) -join ";"
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Host "Added $InstallDir to the user PATH. Open a new terminal before running onelag."
    }
}

Write-Host "OneLag installed to $InstallDir"
Write-Host "Run: onelag scan --output onelag-report.md"
