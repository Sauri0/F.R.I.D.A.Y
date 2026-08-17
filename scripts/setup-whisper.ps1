[CmdletBinding()]
param(
    # small es el default por medición, no por tamaño: en CPU transcribe a ×0,88 del tiempo real y
    # acierta nombres propios, mientras que turbo tarda ×4,88 y castellaniza el rioplatense.
    [ValidateSet('base', 'small', 'turbo', 'turbo-full')]
    [string]$Model = 'small'
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
    # large-v3-turbo cuantizado: ~5x más rápido que large-v3 conservando casi toda la precisión,
    # que es la combinación que sirve para comandos de voz en tiempo real.
    # Los turbo se verifican con el SHA256 que publica el repositorio, que es el hash que expone
    # HuggingFace para archivos LFS; los viejos siguen con el SHA1 con el que ya estaban anotados.
    turbo = @{
        FileName = 'ggml-large-v3-turbo-q5_0.bin'
        Sha256 = '394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2'
    }
    'turbo-full' = @{
        FileName = 'ggml-large-v3-turbo.bin'
        Sha256 = '1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69'
    }
}

$selected = $catalog[$Model]
$modelDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Viernes\Models\Whisper'
$modelDirectory = [IO.Path]::GetFullPath($modelDirectory)
$destinationPath = [IO.Path]::GetFullPath((Join-Path $modelDirectory $selected.FileName))
$sourceUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$($selected.FileName)"

New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null

$algorithm = if ($selected.ContainsKey('Sha256')) { 'SHA256' } else { 'SHA1' }
$expectedHash = if ($algorithm -eq 'SHA256') { $selected.Sha256 } else { $selected.Sha1 }

if (Test-Path -LiteralPath $destinationPath) {
    $existingHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm $algorithm).Hash.ToLowerInvariant()
    if ($existingHash -eq $expectedHash) {
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

    $downloadedHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm $algorithm).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $expectedHash) {
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

