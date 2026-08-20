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

# IncludeNativeLibrariesForSelfExtract va en FALSE, y no es un detalle de empaquetado.
#
# En true, las bibliotecas nativas se meten adentro del ejecutable y se extraen a una carpeta
# temporal al arrancar. Whisper.net no las busca ahi: busca en runtimes\win-x64\native al lado del
# ejecutable. Resultado, medido con el mismo codigo empaquetado de las dos formas:
#
#   normal:                   CARGO en 489 ms
#   monolitico, extract=true: FALLO en 17 ms - "Native Library not found in default paths"
#   monolitico, extract=false: CARGO en 483 ms
#
# Diecisiete milisegundos es demasiado rapido para haber intentado leer un modelo de 465 MB: fallaba
# antes de empezar. Y fallaba EN SILENCIO -- la precarga se traga la excepcion y solo deja
# "ok=False" en la bitacora -- asi que el dictado local quedaba muerto en todo lo que se descargara,
# sin que nada lo dijera.
#
# El costo es que el paquete lleva una carpeta runtimes al lado del ejecutable en vez de un solo
# archivo. El instalador descomprime todo, asi que no cambia nada para quien lo usa.

Push-Location $projectRoot
try {
    dotnet publish $appProject `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained $selfContained `
        --output $outputDirectory `
        --artifacts-path $buildArtifacts `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=false `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló con código $LASTEXITCODE." }
    Write-Host "Viernes publicado en: $outputDirectory"
}
finally {
    Pop-Location
}
