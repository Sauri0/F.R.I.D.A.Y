<#
.SYNOPSIS
    Rehace el icono de la aplicacion a partir de Assets\Viernes.svg.

.DESCRIPTION
    El .ico y los PNG sueltos viajan versionados en el repositorio porque el compilador los necesita
    antes de que exista un ejecutable, y porque un icono no cambia todas las semanas. Esto se corre a
    mano cuando cambia el dibujo.

    Rasteriza con el motor de un navegador —Edge o Chrome, sin ventana— porque ninguno de los dos
    entornos que ya usa el proyecto sabe leer SVG: WPF no lo parsea y .NET no trae rasterizador.

.NOTES
    Los tamanos son los que Windows pide: 256 para la vista de iconos extra grandes, 48 para el
    escritorio, 32 para la barra de titulo y el Alt-Tab, 16 para la barra de tareas y el explorador
    en modo lista. Faltando uno, Windows lo interpola del mas cercano y se nota.
#>
[CmdletBinding()]
param(
    [string] $Svg,
    [string] $Destino
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Los valores por omision se calculan aca y no en el bloque param: bajo Windows PowerShell 5.1,
# $PSScriptRoot no esta disponible todavia cuando se evaluan las omisiones de los parametros.
$raiz = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($Svg)) { $Svg = Join-Path $raiz '..\src\Viernes.App\Assets\Viernes.svg' }
if ([string]::IsNullOrWhiteSpace($Destino)) { $Destino = Join-Path $raiz '..\src\Viernes.App\Assets' }

$Tamanos = @(256, 128, 64, 48, 32, 16)

function Buscar-Navegador {
    $candidatos = @(
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
    )

    foreach ($c in $candidatos) {
        if (Test-Path -LiteralPath $c) { return $c }
    }

    throw 'No encontre Edge ni Chrome, y hace falta uno para rasterizar el SVG.'
}

$navegador = Buscar-Navegador
$svgTexto = Get-Content -LiteralPath $Svg -Raw
$temporal = Join-Path $env:TEMP "viernes-icono-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $temporal -Force | Out-Null

Write-Host "  Rasterizando con $(Split-Path $navegador -Leaf)" -ForegroundColor Gray

$capas = @()
foreach ($lado in $Tamanos) {
    # Una pagina por tamano, del tamano exacto y sin margenes: la captura sale del tamano de la
    # ventana, asi que la pagina TIENE que medir lo mismo que el icono.
    $pagina = Join-Path $temporal "$lado.html"
    $html = '<!doctype html><meta charset="utf-8"><style>html,body{margin:0;padding:0;overflow:hidden}' +
            "svg{display:block;width:${lado}px;height:${lado}px}</style>" + $svgTexto
    Set-Content -LiteralPath $pagina -Value $html -Encoding UTF8

    $png = Join-Path $temporal "$lado.png"
    $argumentos = @(
        '--headless', '--disable-gpu', '--hide-scrollbars',
        '--default-background-color=00000000',
        "--window-size=$lado,$lado",
        "--screenshot=$png",
        ('file:///' + ($pagina -replace '\\', '/'))
    )

    # El navegador escribe ruido en el error estandar aunque le salga todo bien, y con
    # $ErrorActionPreference = 'Stop' eso solo alcanza para abortar. Lo que decide si anduvo es si el
    # archivo esta, no si el navegador se quejo.
    $anterior = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $navegador @argumentos 2>&1 | Out-Null }
    finally { $ErrorActionPreference = $anterior }

    if (-not (Test-Path -LiteralPath $png)) { throw "No se pudo rasterizar el tamano $lado." }

    Copy-Item -LiteralPath $png -Destination (Join-Path $Destino "orbe-$lado.png") -Force
    $capas += , ([IO.File]::ReadAllBytes($png))
    "    {0,3}x{1,-3}  {2,6} bytes" -f $lado, $lado, $capas[-1].Length
}

# El .ico: una tabla de contenidos y los PNG pegados atras. Windows acepta PNG adentro desde Vista.
# El 256 se escribe como 0 en el byte del tamano, que es lo que manda el formato: un byte no llega a
# 256, y ese cero significa «doscientos cincuenta y seis», no «cero».
$salida = New-Object IO.MemoryStream
$escritor = New-Object IO.BinaryWriter($salida)
$escritor.Write([int16]0)
$escritor.Write([int16]1)
$escritor.Write([int16]$Tamanos.Count)

$offset = 6 + (16 * $Tamanos.Count)
for ($i = 0; $i -lt $Tamanos.Count; $i++) {
    $b = if ($Tamanos[$i] -ge 256) { 0 } else { $Tamanos[$i] }
    $escritor.Write([byte]$b)
    $escritor.Write([byte]$b)
    $escritor.Write([byte]0)
    $escritor.Write([byte]0)
    $escritor.Write([int16]1)
    $escritor.Write([int16]32)
    $escritor.Write([int32]$capas[$i].Length)
    $escritor.Write([int32]$offset)
    $offset += $capas[$i].Length
}

foreach ($capa in $capas) { $escritor.Write($capa) }
$escritor.Flush()

$ico = Join-Path $Destino 'Viernes.ico'
[IO.File]::WriteAllBytes($ico, $salida.ToArray())
$escritor.Dispose()
$salida.Dispose()
Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "  Listo: $ico ($((Get-Item $ico).Length) bytes)" -ForegroundColor Green
