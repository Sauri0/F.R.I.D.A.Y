[CmdletBinding()]
param(
    [ValidateSet('base', 'small')]
    [string]$Model = 'base'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$catalog = @{
    base = @{
        FileName = 'ggml-base.bin'
        Sha1 = '465707469ff3a37a2b9b8d8f89f2f99de7299dac'
    }
    small = @{
        FileName = 'ggml-small.bin'
        Sha1 = '55356645c2b361a969dfd0ef2c5a50d530afd8d5'
    }
}

$selected = $catalog[$Model]
$modelDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Viernes\Models\Whisper'
$modelDirectory = [IO.Path]::GetFullPath($modelDirectory)
$destinationPath = [IO.Path]::GetFullPath((Join-Path $modelDirectory $selected.FileName))
$sourceUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$($selected.FileName)"

New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null

if (Test-Path -LiteralPath $destinationPath) {
    $existingHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA1).Hash.ToLowerInvariant()
    if ($existingHash -eq $selected.Sha1) {
        Write-Host "Whisper $Model ya está instalado y verificado en: $destinationPath"
        return
    }

    throw "Ya existe un archivo de modelo pero su hash no coincide. No se modificó: $destinationPath"
}

$temporaryPath = [IO.Path]::GetFullPath((Join-Path $modelDirectory "$($selected.FileName).$([guid]::NewGuid().ToString('N')).download"))
if (-not $temporaryPath.StartsWith($modelDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'La ruta temporal calculada salió del directorio de modelos.'
}

try {
    Write-Host "Descargando Whisper $Model desde el repositorio oficial de whisper.cpp…"
    Invoke-WebRequest -Uri $sourceUri -OutFile $temporaryPath

    $downloadedHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA1).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $selected.Sha1) {
        throw "El hash del modelo descargado no coincide con el publicado por whisper.cpp."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $destinationPath
    Write-Host "Whisper $Model instalado y verificado en: $destinationPath"
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

