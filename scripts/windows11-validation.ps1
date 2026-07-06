param(
    [string] $BinDir = "",
    [switch] $RequireWindows11
)

$ErrorActionPreference = "Stop"

function Resolve-OneLagBinary {
    param(
        [string] $Name
    )

    if (-not [string]::IsNullOrWhiteSpace($BinDir)) {
        $candidate = Join-Path $BinDir $Name
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    $fromPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    throw "$Name was not found. Pass -BinDir with a published OneLag folder."
}

function Invoke-Checked {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [int[]] $AllowedExitCodes = @(0)
    )

    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $exitCode"
    }
}

function Invoke-GuiSmoke {
    param(
        [string] $FilePath
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList "--smoke" -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$FilePath --smoke failed with exit code $($process.ExitCode)"
    }
}

function Assert-Windows11IfRequired {
    $os = Get-CimInstance Win32_OperatingSystem
    $caption = [string] $os.Caption
    $build = [int] $os.BuildNumber
    Write-Host "OS caption: $caption"
    Write-Host "OS build: $build"

    if ($RequireWindows11 -and ($caption -notmatch "Windows 11" -or $build -lt 22000)) {
        throw "Windows 11 validation was required, but this runner is '$caption' build '$build'."
    }
}

Assert-Windows11IfRequired

$onelag = Resolve-OneLagBinary "onelag.exe"
$gui = Resolve-OneLagBinary "onelag-gui.exe"
$work = Join-Path $env:TEMP ("OneLagWin11Validation-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    Write-Host "Using OneLag CLI: $onelag"
    Write-Host "Using OneLag GUI: $gui"
    Invoke-Checked $onelag @("version")
    Invoke-GuiSmoke $gui

    $scanRoot = Join-Path $work "OneDriveValidationRoot"
    $documents = Join-Path $scanRoot "Documents"
    New-Item -ItemType Directory -Force -Path $documents | Out-Null
    "content" | Set-Content -Path (Join-Path $documents "file.txt")

    $report = Join-Path $work "onelag-validation-report.md"
    Invoke-Checked $onelag @("scan", "--root", $scanRoot, "--output", $report, "--max-items", "1000")
    if (-not (Test-Path $report)) {
        throw "scan did not create report"
    }

    Invoke-Checked $onelag @("view", "--report", $report)

    $watch = Join-Path $work "watch"
    $watchReport = Join-Path $work "onelag-watch-validation-report.md"
    Invoke-Checked $onelag @("watch", "start", "--duration", "1s", "--interval", "1s", "--output", $watch)
    Invoke-Checked $onelag @("watch", "mark", "--output", $watch, "--note", "windows-validation")
    Invoke-Checked $onelag @("watch", "report", "--output", $watch, "--report", $watchReport)
    Invoke-Checked $onelag @("view", "--report", $watchReport, "--timeline")

    $bundle = Join-Path $work "onelag-support-bundle"
    Invoke-Checked $onelag @("support", "bundle", "--report", $report, "--report", $watchReport, "--output", $bundle, "--zip", "--include-trace-plan")
    if (-not (Test-Path (Join-Path $bundle "ANALYZE_WITH_CODEX_OR_CLAUDE.md"))) {
        throw "support bundle did not create analysis prompt"
    }

    if (-not (Test-Path "$bundle.zip")) {
        throw "support bundle did not create zip"
    }

    $moveSource = Join-Path $work "OneDriveMoveSource"
    $moveDestination = Join-Path $work "LocalMoveDestination"
    New-Item -ItemType Directory -Force -Path $moveSource | Out-Null
    "move-content" | Set-Content -Path (Join-Path $moveSource "file.txt")

    $movePlan = Join-Path $work "move-plan"
    Invoke-Checked $onelag @("remediate", "move-plan", "--source", $moveSource, "--destination", $moveDestination, "--output", $movePlan)
    Invoke-Checked $onelag @("remediate", "move", "--source", $moveSource, "--destination", $moveDestination)
    Invoke-Checked $onelag @("remediate", "move", "--source", $moveSource, "--destination", $moveDestination, "--execute", "--i-understand-moves-files")
    Invoke-Checked $onelag @("remediate", "verify", "--source", $moveSource, "--destination", $moveDestination)
    Invoke-Checked $onelag @("remediate", "rollback", "--source", $moveSource, "--destination", $moveDestination, "--execute", "--i-understand-moves-files")

    if (-not (Test-Path $moveSource)) {
        throw "rollback did not restore source"
    }

    if (Test-Path $moveDestination) {
        throw "rollback left destination behind"
    }

    Write-Host "OneLag Windows validation passed."
}
finally {
    if (Test-Path $work) {
        Remove-Item -Path $work -Recurse -Force
    }
}
