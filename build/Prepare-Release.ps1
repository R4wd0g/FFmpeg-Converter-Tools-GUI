param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$Version
)

$ErrorActionPreference = 'Stop'

function Get-AppVersion {
    param(
        [string]$AssemblyInfoPath
    )

    $content = Get-Content -LiteralPath $AssemblyInfoPath -Raw
    if ($content -match 'AssemblyInformationalVersion\("([^"]+)"\)') {
        return $Matches[1]
    }

    throw "Could not find AssemblyInformationalVersion in $AssemblyInfoPath"
}

$ProjectRoot = (Resolve-Path $ProjectRoot).Path
if (-not $Version) {
    $Version = Get-AppVersion -AssemblyInfoPath (Join-Path $ProjectRoot 'Properties\AssemblyInfo.cs')
}

$platformSuffix = switch ($Platform.ToLowerInvariant()) {
    'x64' { 'win_x64' }
    'x86' { 'win_x86' }
    default { "win_$Platform" }
}

$buildOutput = Join-Path $ProjectRoot ("bin\{0}\FFmpegConverterGUI.exe" -f $Configuration)
if (-not (Test-Path -LiteralPath $buildOutput)) {
    throw "Build output not found: $buildOutput"
}

$stagingRoot = Join-Path $ProjectRoot 'release-staging'
$artifactsRoot = Join-Path $ProjectRoot 'artifacts'
$portableStage = Join-Path $stagingRoot 'portable'
$installerStage = Join-Path $stagingRoot 'installer'

foreach ($path in @($portableStage, $installerStage, $artifactsRoot)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $portableStage -Force | Out-Null
New-Item -ItemType Directory -Path $installerStage -Force | Out-Null
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

$copyMap = @(
    @{ Source = $buildOutput; Destination = 'FFmpegConverterGUI.exe' },
    @{ Source = (Join-Path $ProjectRoot 'LICENSE'); Destination = 'LICENSE.txt' }
)

foreach ($stage in @($portableStage, $installerStage)) {
    foreach ($entry in $copyMap) {
        Copy-Item -LiteralPath $entry.Source -Destination (Join-Path $stage $entry.Destination) -Force
    }
}

$portableZip = Join-Path $artifactsRoot ("FFmpeg-Converter-Tools-GUI-{0}-{1}-portable.zip" -f $Version, $platformSuffix)
if (Test-Path -LiteralPath $portableZip) {
    Remove-Item -LiteralPath $portableZip -Force
}

Compress-Archive -Path (Join-Path $portableStage '*') -DestinationPath $portableZip -CompressionLevel Optimal

Write-Host "Prepared portable package: $portableZip"
Write-Host "Installer staging directory: $installerStage"
Write-Host "Version: $Version"
