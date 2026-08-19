[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot 'src\Viernes.App\Viernes.App.csproj'
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($localAppData)) { throw 'LOCALAPPDATA no está disponible.' }
$outputDirectory = Join-Path $localAppData "Viernes\Published\$Runtime"
$buildArtifacts = Join-Path $localAppData 'Viernes\PublishArtifacts'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

Push-Location $projectRoot
try {
    dotnet publish $appProject `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained $selfContained `
        --output $outputDirectory `
        --artifacts-path $buildArtifacts `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló con código $LASTEXITCODE." }
    Write-Host "Viernes publicado en: $outputDirectory"
}
finally {
    Pop-Location
}
