[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$Version,

    [string]$IsccPath,

    [switch]$SkipPublish,

    [string]$SignToolName,

    [string]$SignToolCommand
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'The installer build requires Windows because Inno Setup is a Windows tool.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationProject = Join-Path $repositoryRoot 'src\JKBridge.App\JKBridge.App.csproj'
$installerScript = Join-Path $repositoryRoot 'packaging\windows\jkbridge.iss'
$installerRoot = Join-Path $repositoryRoot 'artifacts\installer'
$payloadDirectory = Join-Path $installerRoot 'payload'
$outputDirectory = Join-Path $installerRoot 'output'
$payloadExecutable = Join-Path $payloadDirectory 'JKBridge.exe'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $applicationProject -Raw
    $versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode) {
        throw "Application version was not found in '$applicationProject'."
    }
    $Version = $versionNode.InnerText
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw "Version '$Version' must contain three or four numeric components for Windows version metadata."
}

if ([string]::IsNullOrWhiteSpace($SignToolName) -ne
    [string]::IsNullOrWhiteSpace($SignToolCommand)) {
    throw 'SignToolName and SignToolCommand must be supplied together.'
}

$dotnetCommand = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    $dotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue
}
if ($null -eq $dotnetCommand) {
    throw '.NET SDK was not found on PATH.'
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $isccCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $isccCommand) {
        $IsccPath = $isccCommand.Source
    }
    else {
        $isccCandidates = @()
        if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
            $isccCandidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
        }
        if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
            $isccCandidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
        }
        $IsccPath = $isccCandidates |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($IsccPath) -or
    -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6.3+ compiler (ISCC.exe) was not found. Install it or pass -IsccPath.'
}

$isccVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($IsccPath)
if ($isccVersion.FileMajorPart -lt 6 -or
    ($isccVersion.FileMajorPart -eq 6 -and $isccVersion.FileMinorPart -lt 3)) {
    throw "Inno Setup 6.3 or later is required; found '$($isccVersion.FileVersion)'."
}

New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

if (-not $SkipPublish) {
    Get-ChildItem -LiteralPath $payloadDirectory -Force -ErrorAction SilentlyContinue |
        Remove-Item -Force -Recurse

    & $dotnetCommand.Source publish $applicationProject `
        --configuration $Configuration `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=embedded `
        --output $payloadDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $payloadExecutable -PathType Leaf)) {
    throw "Installer payload was not found at '$payloadExecutable'."
}

$isccArguments = @(
    "/DMyAppVersion=$Version",
    "/DPayloadDir=$payloadDirectory",
    "/DOutputDir=$outputDirectory"
)

if (-not [string]::IsNullOrWhiteSpace($SignToolName)) {
    $isccArguments += "/DSignToolName=$SignToolName"
    $isccArguments += "/S$SignToolName=$SignToolCommand"
}

$isccArguments += $installerScript
& $IsccPath @isccArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerName = "JKBridge-Setup-win-x64-$Version.exe"
$installerPath = Join-Path $outputDirectory $installerName
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup completed without producing '$installerPath'."
}

$installerHash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256
$checksumPath = "$installerPath.sha256"
"$($installerHash.Hash.ToLowerInvariant())  $installerName" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Installer: $installerPath"
Write-Host "SHA-256:  $checksumPath"
