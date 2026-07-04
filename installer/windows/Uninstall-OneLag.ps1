param(
    [string] $InstallDir = "$env:LOCALAPPDATA\Programs\OneLag"
)

$ErrorActionPreference = "Stop"

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (-not [string]::IsNullOrWhiteSpace($userPath)) {
    $parts = $userPath -split ";" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not $_.Equals($InstallDir, [StringComparison]::OrdinalIgnoreCase)
    }
    [Environment]::SetEnvironmentVariable("Path", ($parts -join ";"), "User")
}

if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
}

Write-Host "OneLag removed from $InstallDir"
