[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectRoot 'Viernes.slnx'
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($localAppData)) { throw 'LOCALAPPDATA no está disponible.' }
$buildArtifacts = Join-Path $localAppData 'Viernes\BuildArtifacts'
$configurationFolder = $Configuration.ToLowerInvariant()
$executablePath = Join-Path $buildArtifacts "bin\Viernes.App\$configurationFolder\Viernes.exe"

Push-Location $projectRoot
try {
    dotnet restore $solutionPath --artifacts-path $buildArtifacts --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore falló con código $LASTEXITCODE." }

    dotnet build $solutionPath --configuration $Configuration --no-restore --artifacts-path $buildArtifacts --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build falló con código $LASTEXITCODE." }

    if (-not (Test-Path -LiteralPath $executablePath)) { throw "No se encontró el ejecutable: $executablePath" }
    & $executablePath
    if ($LASTEXITCODE -ne 0) { throw "Viernes finalizó con código $LASTEXITCODE." }
}
finally {
    Pop-Location
}
