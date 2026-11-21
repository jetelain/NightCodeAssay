<#
.SYNOPSIS
    Packages NightCodeForge.Assay NuGet global tool (and optionally portable Native AOT archive) into an output directory.
.DESCRIPTION
    Packs src/NightCodeAssay.App/NightCodeAssay.App.csproj into NightCodeForge.Assay.<version>.nupkg.
    When -IncludePortable is specified, also publishes a portable Native AOT archive NightCodeAssay-<Runtime>.zip.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER Version
    Optional explicit package/archive version override.
.PARAMETER OutputDir
    Destination directory for generated artifacts (default: ./artifacts).
.PARAMETER IncludePortable
    Also publish portable Native AOT NightCodeAssay-<Runtime>.zip alongside the .nupkg tool package.
.EXAMPLE
    .\package.ps1
.EXAMPLE
    .\package.ps1 -OutputDir ..\NightCodeForge\artifacts\bundle
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$OutputDir = "./artifacts",
    [switch]$IncludePortable
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$appProjectPath = Join-Path $repoRoot "src\NightCodeAssay.App\NightCodeAssay.App.csproj"
if (-not (Test-Path $appProjectPath)) {
    throw "NightCodeAssay.App project not found: $appProjectPath"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projXml = Get-Content $appProjectPath
    $versionNode = $projXml.SelectSingleNode("//Version")
    $Version = if ($versionNode -and -not [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        $versionNode.InnerText.Trim()
    } else {
        "0.1.0"
    }
}

$resolvedOutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

Write-Host "Packing NightCodeForge.Assay ($Version) -> $resolvedOutputDir..." -ForegroundColor Cyan
& dotnet pack $appProjectPath -c $Configuration "-p:Version=$Version" -o $resolvedOutputDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack for NightCodeAssay.App failed with exit code $LASTEXITCODE."
}

if ($IncludePortable) {
    $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ncf-assay-pkg-" + [System.Guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

        Write-Host "Publishing portable Native AOT NightCodeAssay ($Runtime, $Version)..." -ForegroundColor Cyan
        & dotnet publish $appProjectPath `
            -c $Configuration `
            -r $Runtime `
            -p:PublishAot=true `
            -p:StripSymbols=true `
            -p:DebugType=none `
            -p:DebugSymbols=false `
            "-p:Version=$Version" `
            -o $stagingDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish for NightCodeAssay.App failed with exit code $LASTEXITCODE."
        }

        Get-ChildItem -Path $stagingDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Copy-Item -Path (Join-Path $repoRoot "README.md") -Destination $stagingDir -Force
        Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $stagingDir -Force

        $archivePath = Join-Path $resolvedOutputDir "NightCodeAssay-$Runtime.zip"
        if (Test-Path $archivePath) {
            Remove-Item -Path $archivePath -Force
        }
        Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $archivePath -Force
    }
    finally {
        if (Test-Path $stagingDir) {
            Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "NightCodeAssay packaging complete in: $resolvedOutputDir" -ForegroundColor Green
