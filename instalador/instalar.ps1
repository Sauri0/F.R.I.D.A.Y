<#
.SYNOPSIS
    Instala el asistente y lo deja funcionando.

.DESCRIPTION
    Un solo archivo que hace todo: baja la aplicación, baja el modelo de voz, pregunta cómo se va a
    llamar el asistente, guarda la clave de OpenRouter del usuario y crea los accesos directos.

    Volver a correrlo actualiza la instalación sin volver a preguntar nada.

.NOTES
    La clave es del usuario y nunca viaja acá adentro. Se pide en el momento, se guarda como variable
    de entorno de la cuenta de Windows y no se escribe en ningún archivo del proyecto.
#>

[CmdletBinding()]
param(
    # Actualiza la aplicación sin preguntar nada. Para el acceso directo «Actualizar».
    [switch] $Actualizar,

    # Instala sin preguntar, con estos valores. Para pruebas automatizadas.
    [string] $Nombre,
    [switch] $SinModelo
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositorioApi = 'https://api.github.com/repos/Sauri0/F.R.I.D.A.Y/releases/latest'
$ModeloUrl = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin'
$ModeloNombre = 'ggml-small.bin'
$ModeloMinimoBytes = 400MB

# La carpeta de datos NO sigue al nombre elegido. Identifica al producto, no al asistente: si
# siguiera al nombre, renombrarlo abandonaría el historial, las preferencias y el modelo de voz.
$Datos = Join-Path $env:LOCALAPPDATA 'Viernes'
$Aplicacion = Join-Path $Datos 'app'
$Modelos = Join-Path $Datos 'Models\Whisper'

#region presentación

function Titulo($texto) {
    Write-Host ''
    Write-Host "  $texto" -ForegroundColor Cyan
    Write-Host "  $('─' * $texto.Length)" -ForegroundColor DarkCyan
}

function Paso($texto) { Write-Host "  · $texto" -ForegroundColor Gray }
function Listo($texto) { Write-Host "  ✓ $texto" -ForegroundColor Green }
function Aviso($texto) { Write-Host "  ! $texto" -ForegroundColor Yellow }
function Error($texto) { Write-Host "  ✗ $texto" -ForegroundColor Red }

#endregion

#region comprobaciones

function Confirmar-Equipo {
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'Esta aplicación necesita Windows de 64 bits.'
    }

    if ([Environment]::OSVersion.Version.Major -lt 10) {
        throw 'Esta aplicación necesita Windows 10 o posterior.'
    }

    # Un instalador que corre como administrador deja la instalación con permisos que después el
    # usuario normal no puede tocar —ni actualizar—. Todo esto va en la carpeta del usuario, así que
    # elevar no aporta nada y sí molesta.
    $identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identidad)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Aviso 'Estás como administrador. No hace falta: todo se instala en tu carpeta de usuario.'
    }
}

#endregion

#region el nombre

<#
    Las mismas reglas que AssistantIdentity en el código. Si se separan, el instalador va a aceptar
    un nombre que la aplicación después normaliza a otra cosa, y el usuario va a ver que su asistente
    se llama distinto de lo que escribió.
#>
function Probar-Nombre([string] $candidato) {
    $limpio = if ($null -eq $candidato) { '' } else { $candidato.Trim() }

    if ($limpio.Length -eq 0) { return 'Escribí un nombre.' }
    if ($limpio.Length -lt 2) { return 'El nombre necesita al menos dos letras.' }
    if ($limpio.Length -gt 24) { return 'El nombre no puede pasar de 24 caracteres.' }
    if ($limpio -match '\d') { return 'Sin números: el nombre se dice en voz alta y no se reconocen bien.' }
    if ($limpio -notmatch '\p{L}') { return 'El nombre tiene que tener letras.' }
    if ($limpio -match "[^\p{L} \-'’]") { return 'Usá sólo letras, espacios, guiones o apóstrofos.' }

    return $null
}

function Pedir-Nombre {
    Titulo '¿Cómo querés que se llame?'
    Write-Host '  Es el nombre con el que lo vas a despertar. Podés cambiarlo después.' -ForegroundColor DarkGray
    Write-Host ''

    while ($true) {
        $respuesta = Read-Host '  Nombre (Enter para «Viernes»)'
        if ([string]::IsNullOrWhiteSpace($respuesta)) { $respuesta = 'Viernes' }

        $problema = Probar-Nombre $respuesta
        if ($null -eq $problema) {
            $nombre = (Get-Culture).TextInfo.ToTitleCase($respuesta.Trim().ToLower())

            # Se muestran las frases porque la regla de dos palabras sorprende: alguien que escribe
            # «Ana» espera despertarlo diciendo «Ana». Verlo ahora evita el «no me escucha» después.
            Write-Host ''
            Write-Host "  Lo vas a despertar diciendo:  " -NoNewline -ForegroundColor Gray
            Write-Host "«Hola $nombre»  «Che $nombre»  «Ey $nombre»" -ForegroundColor White
            Write-Host '  Siempre dos palabras: el nombre solo aparece en cualquier charla' -ForegroundColor DarkGray
            Write-Host '  y lo despertaría sin querer.' -ForegroundColor DarkGray
            return $nombre
        }

        Error $problema
    }
}

#endregion

#region la clave

<#
    La clave es del usuario, no del proyecto. Se guarda como variable de entorno de la cuenta —no en
    un archivo— para que no termine en una copia de seguridad, en un repositorio ni en un adjunto por
    accidente. Se lee como texto oculto y nunca se muestra ni se registra.
#>
function Pedir-Clave {
    $actual = [Environment]::GetEnvironmentVariable('OPENROUTER_API_KEY', 'User')
    if (-not [string]::IsNullOrWhiteSpace($actual)) {
        Listo 'Ya tenés una clave de OpenRouter configurada.'
        $cambiar = Read-Host '  ¿La reemplazás? (s/N)'
        if ($cambiar -notmatch '^[sS]') { return }
    }

    Titulo 'Tu clave de OpenRouter'
    Write-Host '  Sacala gratis en https://openrouter.ai/keys — empieza con «sk-or-».' -ForegroundColor DarkGray
    Write-Host '  Queda guardada en tu cuenta de Windows. No se escribe en ningún archivo' -ForegroundColor DarkGray
    Write-Host '  ni se comparte con nadie.' -ForegroundColor DarkGray
    Write-Host ''

    while ($true) {
        $segura = Read-Host '  Clave (no se ve mientras escribís)' -AsSecureString
        $clave = [Runtime.InteropServices.Marshal]::PtrToStringUni(
            [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($segura)).Trim()

        if ([string]::IsNullOrWhiteSpace($clave)) {
            Aviso 'Sin clave el asistente arranca pero no puede pensar. Podés ponerla después.'
            $seguir = Read-Host '  ¿Seguir sin clave? (s/N)'
            if ($seguir -match '^[sS]') { return }
            continue
        }

        if ($clave -notmatch '^sk-or-') {
            Aviso 'Eso no parece una clave de OpenRouter (tendría que empezar con «sk-or-»).'
            $igual = Read-Host '  ¿La guardo igual? (s/N)'
            if ($igual -notmatch '^[sS]') { continue }
        }

        [Environment]::SetEnvironmentVariable('OPENROUTER_API_KEY', $clave, 'User')

        # También en este proceso, para que el asistente arranque al final sin cerrar sesión.
        $env:OPENROUTER_API_KEY = $clave
        $clave = $null
        Listo 'Clave guardada en tu cuenta de Windows.'
        return
    }
}

#endregion

#region descargas

function Bajar-Archivo([string] $url, [string] $destino, [string] $que) {
    $parcial = "$destino.parcial"
    if (Test-Path -LiteralPath $parcial) { Remove-Item -LiteralPath $parcial -Force }

    Paso "Bajando $que..."
    $anterior = $ProgressPreference
    try {
        # Sin la barra de progreso, Invoke-WebRequest baja archivos grandes órdenes de magnitud más
        # rápido: el redibujado de la consola domina el tiempo de descarga.
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $parcial -UseBasicParsing -MaximumRedirection 5
    }
    finally {
        $ProgressPreference = $anterior
    }

    # El movimiento final es lo que hace que una descarga cortada no quede como archivo válido:
    # si se corta la luz a mitad, queda un .parcial y la próxima corrida vuelve a bajarlo.
    Move-Item -LiteralPath $parcial -Destination $destino -Force
}

function Instalar-Aplicacion {
    Titulo 'La aplicación'

    Paso 'Buscando la última versión...'
    $cabeceras = @{ 'User-Agent' = 'instalador-viernes'; 'Accept' = 'application/vnd.github+json' }
    try {
        $release = Invoke-RestMethod -Uri $RepositorioApi -Headers $cabeceras -UseBasicParsing
    }
    catch {
        throw "No pude consultar las versiones publicadas: $($_.Exception.Message)"
    }

    $paquete = $release.assets | Where-Object { $_.name -like '*win-x64*.zip' } | Select-Object -First 1
    if ($null -eq $paquete) {
        throw 'La última versión no tiene un paquete para Windows. Avisale al autor.'
    }

    $instalada = Join-Path $Datos 'version.txt'
    if ((Test-Path -LiteralPath $instalada) -and
        ((Get-Content -LiteralPath $instalada -Raw).Trim() -eq $release.tag_name) -and
        (Test-Path -LiteralPath (Join-Path $Aplicacion 'Viernes.exe'))) {
        Listo "Ya tenés la última versión ($($release.tag_name))."
        return
    }

    $zip = Join-Path $env:TEMP "viernes-$($release.tag_name).zip"
    Bajar-Archivo $paquete.browser_download_url $zip "la aplicación ($([math]::Round($paquete.size / 1MB)) MB)"

    Paso 'Instalando...'
    Detener-Aplicacion

    # Se extrae al lado y después se cambia de lugar: si la extracción falla a mitad, la instalación
    # anterior sigue entera en vez de quedar medio sobrescrita y sin arrancar.
    $nueva = "$Aplicacion.nueva"
    if (Test-Path -LiteralPath $nueva) { Remove-Item -LiteralPath $nueva -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $nueva -Force

    if (-not (Test-Path -LiteralPath (Join-Path $nueva 'Viernes.exe'))) {
        Remove-Item -LiteralPath $nueva -Recurse -Force
        throw 'El paquete descargado no trae Viernes.exe. No lo instalo.'
    }

    if (Test-Path -LiteralPath $Aplicacion) { Remove-Item -LiteralPath $Aplicacion -Recurse -Force }
    Move-Item -LiteralPath $nueva -Destination $Aplicacion
    Set-Content -LiteralPath $instalada -Value $release.tag_name -NoNewline
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

    Listo "Aplicación instalada ($($release.tag_name))."
}

function Instalar-Modelo {
    Titulo 'El oído'

    $destino = Join-Path $Modelos $ModeloNombre
    if ((Test-Path -LiteralPath $destino) -and
        ((Get-Item -LiteralPath $destino).Length -ge $ModeloMinimoBytes)) {
        Listo 'El modelo de voz ya está.'
        return
    }

    Write-Host '  Escucha en tu máquina, sin mandar audio a ningún lado. Son 488 MB' -ForegroundColor DarkGray
    Write-Host '  y se bajan una sola vez.' -ForegroundColor DarkGray

    New-Item -ItemType Directory -Path $Modelos -Force | Out-Null
    Bajar-Archivo $ModeloUrl $destino 'el modelo de voz (488 MB)'

    $tamaño = (Get-Item -LiteralPath $destino).Length
    if ($tamaño -lt $ModeloMinimoBytes) {
        Remove-Item -LiteralPath $destino -Force
        throw "La descarga del modelo quedó incompleta ($([math]::Round($tamaño / 1MB)) MB). Volvé a correr el instalador."
    }

    Listo 'Modelo de voz instalado.'
}

#endregion

#region configuración

function Guardar-Nombre([string] $nombre) {
    New-Item -ItemType Directory -Path $Datos -Force | Out-Null
    $ruta = Join-Path $Datos 'settings.json'

    # Se respeta lo que ya había: quien reinstala no pierde su forma de orbe ni su micrófono
    # silenciado sólo porque cambió el nombre.
    $preferencias = if (Test-Path -LiteralPath $ruta) {
        try { Get-Content -LiteralPath $ruta -Raw | ConvertFrom-Json -AsHashtable }
        catch { @{} }
    } else { @{} }

    $preferencias['schemaVersion'] = 1
    $preferencias['assistantName'] = $nombre

    # Las frases se borran a propósito: se derivan del nombre. Dejarlas escritas congelaría las del
    # nombre anterior y el asistente seguiría respondiendo al viejo, o a ninguno.
    $preferencias.Remove('wakeWordPhrases')

    $preferencias | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ruta -Encoding UTF8
    Listo "Se va a llamar $nombre."
}

function Crear-Accesos([string] $nombre) {
    Titulo 'Accesos'

    $exe = Join-Path $Aplicacion 'Viernes.exe'
    $shell = New-Object -ComObject WScript.Shell

    $menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    foreach ($carpeta in @($menu, [Environment]::GetFolderPath('Desktop'))) {
        # Los accesos viejos se borran por nombre de archivo: si el usuario renombró el asistente,
        # quedarían dos íconos y uno con el nombre que ya no usa.
        Get-ChildItem -LiteralPath $carpeta -Filter '*.lnk' -ErrorAction SilentlyContinue |
            Where-Object {
                try { $shell.CreateShortcut($_.FullName).TargetPath -eq $exe } catch { $false }
            } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }

        $acceso = $shell.CreateShortcut((Join-Path $carpeta "$nombre.lnk"))
        $acceso.TargetPath = $exe
        $acceso.WorkingDirectory = $Aplicacion
        $acceso.Description = "$nombre, tu asistente"
        $acceso.Save()
    }

    Listo 'Acceso en el menú Inicio y en el escritorio.'
}

function Ofrecer-Arranque {
    $respuesta = Read-Host '  ¿Que arranque solo con Windows? (S/n)'
    if ($respuesta -match '^[nN]') {
        Paso 'No arranca solo. Lo podés activar después desde el ícono de la bandeja.'
        return
    }

    $clave = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $clave -Name 'Viernes' -Value "`"$(Join-Path $Aplicacion 'Viernes.exe')`""
    Listo 'Va a arrancar con Windows.'
}

function Detener-Aplicacion {
    Get-Process -Name 'Viernes' -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 600
}

#endregion

#region principal

try {
    Clear-Host
    Write-Host ''
    Write-Host '   ┌─────────────────────────────────────────────┐' -ForegroundColor Cyan
    Write-Host '   │  Tu asistente de escritorio                 │' -ForegroundColor Cyan
    Write-Host '   │  Vive en una gota, escucha y hace cosas.     │' -ForegroundColor Cyan
    Write-Host '   └─────────────────────────────────────────────┘' -ForegroundColor Cyan

    Confirmar-Equipo

    if ($Actualizar) {
        Instalar-Aplicacion
        Listo 'Actualizado.'
        Start-Process (Join-Path $Aplicacion 'Viernes.exe')
        exit 0
    }

    $elegido = if ($Nombre) {
        $problema = Probar-Nombre $Nombre
        if ($problema) { throw $problema }
        (Get-Culture).TextInfo.ToTitleCase($Nombre.Trim().ToLower())
    } else {
        Pedir-Nombre
    }

    if (-not $Nombre) { Pedir-Clave }

    Instalar-Aplicacion
    if (-not $SinModelo) { Instalar-Modelo }

    Titulo 'Últimos detalles'
    Guardar-Nombre $elegido
    Crear-Accesos $elegido
    if (-not $Nombre) { Ofrecer-Arranque }

    Write-Host ''
    Write-Host "   Listo. Decile «Hola $elegido» y esperá a que la gota reaccione." -ForegroundColor Green
    Write-Host ''

    if (-not $Nombre) {
        $abrir = Read-Host '  ¿Lo abro ahora? (S/n)'
        if ($abrir -notmatch '^[nN]') {
            Start-Process (Join-Path $Aplicacion 'Viernes.exe')
        }
    }

    exit 0
}
catch {
    Write-Host ''
    Error $_.Exception.Message
    Write-Host ''
    Write-Host '  Nada quedó a medias: volvé a correr el instalador cuando quieras.' -ForegroundColor DarkGray
    Write-Host ''
    if (-not $Nombre) { Read-Host '  Enter para cerrar' | Out-Null }
    exit 1
}

#endregion
