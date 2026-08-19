<#
.SYNOPSIS
    Instala el asistente y lo deja funcionando.

.DESCRIPTION
    Un solo archivo que hace todo: baja la aplicación, baja el modelo de voz, pregunta cómo se va a
    llamar el asistente, guarda sus claves y crea los accesos directos.

    Volver a correrlo actualiza la instalación sin volver a preguntar nada.

.NOTES
    Las claves son del usuario y ninguna viaja acá adentro. Las dos se piden en el momento, se leen
    como texto oculto, no se muestran, no se registran y no se escriben en ningún archivo del
    proyecto. La de OpenRouter va a las variables de entorno de la cuenta de Windows; la de Google
    —que es opcional— al archivo de claves de la carpeta de datos, porque así lo pidió quien lo usa:
    abrir, pegar y guardar es más simple que aprender setx.
#>

[CmdletBinding()]
param(
    # Actualiza la aplicación sin preguntar nada. Para el acceso directo «Actualizar».
    [switch] $Actualizar,

    # Instala sin preguntar, con estos valores. Para pruebas automatizadas.
    [string] $Nombre,
    [switch] $SinModelo,

    # Comprueba un nombre y sale sin instalar nada. Ver el bloque principal para el porqué.
    [string] $ProbarNombre
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositorioApi = 'https://api.github.com/repos/Sauri0/F.R.I.D.A.Y/releases/latest'
$ModeloUrl = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin'
$ModeloNombre = 'ggml-small.bin'
$ModeloMinimoBytes = 400MB

# El detector de voz entrenado. Sin esto la aplicación cae a una heurística que mide energía y cruces
# por cero, y esa heurística no distingue una voz de un aplauso ni de un portazo: se interrumpe sola.
# La versión está fija a propósito —el nombre de los archivos adentro del paquete cambió entre
# versiones de ONNX Runtime— y los tamaños se comprobaron uno por uno contra los paquetes reales.
$VadVersion = '1.23.2'
$SileroUrl = 'https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx'
$SileroMinimoBytes = 2MB
$OnnxAdministradoUrl = "https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime.Managed/$VadVersion"
$OnnxNativoUrl = "https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime/$VadVersion"

# La carpeta de datos NO sigue al nombre elegido. Identifica al producto, no al asistente: si
# siguiera al nombre, renombrarlo abandonaría el historial, las preferencias y el modelo de voz.
$Datos = Join-Path $env:LOCALAPPDATA 'Viernes'
$Aplicacion = Join-Path $Datos 'app'
$Modelos = Join-Path $Datos 'Models\Whisper'
$Deteccion = Join-Path $Datos 'Models\Vad'
$Claves = Join-Path $Datos 'claves.json'

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
    Las mismas reglas que AssistantIdentity en el código, en el mismo orden y con los mismos
    mensajes. Si se separan, el instalador va a aceptar un nombre que la aplicación después normaliza
    a otra cosa, y el usuario va a ver que su asistente se llama distinto de lo que escribió.

    No se pueden compartir de verdad —esto corre antes de que la aplicación exista en el disco—, así
    que lo que las mantiene juntas es una prueba: AssistantNameRulesMatchInstallerTests corre este
    mismo archivo con -ProbarNombre y compara veredicto y forma final contra los del código.
#>
function Probar-Nombre([string] $candidato) {
    $limpio = if ($null -eq $candidato) { '' } else { $candidato.Trim() }

    if ($limpio.Length -eq 0) { return 'Escribí un nombre.' }
    if ($limpio.Length -lt 2) { return 'El nombre necesita al menos dos letras.' }
    if ($limpio.Length -gt 24) { return 'El nombre no puede pasar de 24 caracteres.' }
    if ($limpio -match '\d') { return 'Sin números: el nombre se dice en voz alta y los números no se reconocen bien.' }
    if ($limpio -notmatch '\p{L}') { return 'El nombre tiene que tener letras.' }
    if ($limpio -match "[^\p{L} \-'’]") { return 'Usá sólo letras, espacios, guiones o apóstrofos.' }

    return $null
}

<#
    Deja el nombre en la misma forma canónica que AssistantIdentity.Normalize.

    Acá había un ToTitleCase, que parece lo mismo y no lo es: ToTitleCase baja todo a minúscula antes
    de subir la primera letra de cada palabra, así que «JARVIS» se guardaba como «Jarvis» y «McCoy»
    como «Mccoy». El código respeta esas mayúsculas a propósito —quien las escribió las eligió—, con
    lo cual el instalador guardaba un nombre y la aplicación mostraba otro.
#>
function Normalizar-Nombre([string] $candidato) {
    # Los espacios internos se colapsan: «Ana   María» y «Ana María» tienen que despertar igual.
    $colapsado = ($candidato.Trim() -split '\s+' | Where-Object { $_.Length -gt 0 }) -join ' '

    $armado = [Text.StringBuilder]::new($colapsado.Length)
    $inicioDePalabra = $true
    foreach ($caracter in $colapsado.ToCharArray()) {
        $letra = if ($inicioDePalabra -and [char]::IsLower($caracter)) {
            [char]::ToUpper($caracter, [Globalization.CultureInfo]::CurrentCulture)
        } else {
            $caracter
        }

        [void] $armado.Append($letra)

        # El apóstrofo no abre palabra: «D'Ana» no es «D'ana» con mayúscula en la a.
        $inicioDePalabra = ($caracter -eq ' ' -or $caracter -eq '-')
    }

    return $armado.ToString()
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
            $nombre = Normalizar-Nombre $respuesta

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
    Lee una clave sin mostrarla, y suelta el texto plano apenas deja de hacer falta.

    Está acá afuera porque las dos claves se piden igual y una sola forma de pedirlas es una sola
    forma de equivocarse. Read-Host -AsSecureString no alcanza por sí solo: para poder guardarla hay
    que sacarla de la SecureString, y eso deja una copia en memoria no administrada que nadie libera
    si no se lo pide. Acá se libera en el finally, pase lo que pase.
#>
function Leer-Clave-Oculta([string] $etiqueta) {
    $segura = Read-Host $etiqueta -AsSecureString
    $puntero = [IntPtr]::Zero
    try {
        $puntero = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($segura)
        return [Runtime.InteropServices.Marshal]::PtrToStringUni($puntero).Trim()
    }
    finally {
        if ($puntero -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($puntero)
        }

        $segura.Dispose()
    }
}

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
        $clave = Leer-Clave-Oculta '  Clave (no se ve mientras escribís)'

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

        # Un tope por defecto. Sin ninguno configurado, el guardián de presupuesto no tiene contra
        # qué comparar y deja pasar todos los turnos: existe el aparato entero, medido y con interfaz,
        # y no frena nunca. Dos dólares por día son unos dos mil turnos al precio actual —muy por
        # encima del uso normal—, así que no molesta y sí evita la sorpresa de un bucle.
        if (-not [Environment]::GetEnvironmentVariable('VIERNES_DAILY_BUDGET', 'User')) {
            [Environment]::SetEnvironmentVariable('VIERNES_DAILY_BUDGET', '2', 'User')
            $env:VIERNES_DAILY_BUDGET = '2'
            Paso 'Tope de gasto: USD 2 por día. Se cambia con la variable VIERNES_DAILY_BUDGET.'
        }

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
        # Un 404 acá casi nunca es «no existe»: es un repositorio privado visto sin credenciales, y
        # GitHub contesta lo mismo en los dos casos a propósito. Decir «no encontrado» mandaría a
        # buscar un problema de red que no existe.
        # Sin la comprobación, una falla que no sea HTTP —sin red, sin DNS, un proxy que no atiende—
        # rebota contra el modo estricto al pedir una propiedad que esa excepción no tiene, y lo que
        # se ve es «The property 'Response' cannot be found» en vez del motivo real. El peor mensaje
        # posible es el que manda a buscar el problema donde no está.
        $respuesta = if ($_.Exception.PSObject.Properties.Name -contains 'Response') {
            $_.Exception.Response
        } else {
            $null
        }

        $codigo = if ($null -ne $respuesta) { $respuesta.StatusCode.value__ } else { 0 }
        if ($codigo -eq 404) {
            throw @'
El repositorio no está accesible. Puede ser que sea privado y no tengas permiso.

Si es tuyo, hacelo público o instalá desde el código:
  git clone https://github.com/Sauri0/F.R.I.D.A.Y
  dotnet publish src/Viernes.App -c Release -r win-x64 --self-contained
'@
        }

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

<#
    Saca un archivo de adentro de un paquete de NuGet, que es un zip con otro nombre.
#>
function Extraer-Del-Paquete([string] $paquete, [string] $adentro, [string] $destino) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($paquete)
    try {
        $entrada = $zip.Entries | Where-Object { $_.FullName -eq $adentro } | Select-Object -First 1
        if ($null -eq $entrada) {
            throw "El paquete no trae «$adentro». Puede haber cambiado de nombre en otra versión."
        }

        [IO.Compression.ZipFileExtensions]::ExtractToFile($entrada, $destino, $true)
    }
    finally {
        $zip.Dispose()
    }
}

function Instalar-Deteccion {
    Titulo 'Distinguir tu voz de un ruido'

    $silero = Join-Path $Deteccion 'silero_vad.onnx'
    $administrado = Join-Path $Deteccion 'Microsoft.ML.OnnxRuntime.dll'
    $nativo = Join-Path $Deteccion 'onnxruntime.dll'
    $compartido = Join-Path $Deteccion 'onnxruntime_providers_shared.dll'

    $completo = (Test-Path -LiteralPath $silero) -and
        ((Get-Item -LiteralPath $silero).Length -ge $SileroMinimoBytes) -and
        (Test-Path -LiteralPath $administrado) -and
        (Test-Path -LiteralPath $nativo) -and
        (Test-Path -LiteralPath $compartido)

    if ($completo) {
        Listo 'El detector de voz ya está.'
        return
    }

    Write-Host '  Un aplauso, un portazo o la tele no la interrumpen: hay un detector' -ForegroundColor DarkGray
    Write-Host '  entrenado que separa la voz del ruido, y corre en tu máquina.' -ForegroundColor DarkGray

    New-Item -ItemType Directory -Path $Deteccion -Force | Out-Null

    if (-not (Test-Path -LiteralPath $silero) -or
        (Get-Item -LiteralPath $silero).Length -lt $SileroMinimoBytes) {
        Bajar-Archivo $SileroUrl $silero 'el detector de voz (2 MB)'
    }

    # Los dos paquetes se bajan a un temporal y se tiran: de los 124 MB quedan 14 en disco. Se hace
    # así y no con una descarga directa porque los binarios oficiales de ONNX Runtime sólo se
    # publican adentro de estos paquetes, y una copia propia en otro lado sería una cadena de
    # suministro más para vigilar.
    $temporal = Join-Path ([IO.Path]::GetTempPath()) ("viernes-vad-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporal -Force | Out-Null
    try {
        if (-not (Test-Path -LiteralPath $administrado)) {
            $paquete = Join-Path $temporal 'administrado.zip'
            Bajar-Archivo $OnnxAdministradoUrl $paquete 'la parte administrada (1 MB)'
            Extraer-Del-Paquete $paquete 'lib/netstandard2.0/Microsoft.ML.OnnxRuntime.dll' $administrado
        }

        if (-not (Test-Path -LiteralPath $nativo) -or -not (Test-Path -LiteralPath $compartido)) {
            $paquete = Join-Path $temporal 'nativo.zip'
            Bajar-Archivo $OnnxNativoUrl $paquete 'el motor de inferencia (124 MB, quedan 14)'
            Extraer-Del-Paquete $paquete 'runtimes/win-x64/native/onnxruntime.dll' $nativo
            Extraer-Del-Paquete $paquete 'runtimes/win-x64/native/onnxruntime_providers_shared.dll' $compartido
        }
    }
    finally {
        Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue
    }

    Listo 'Detector de voz instalado.'
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

<#
    Deja el archivo donde vive la clave de Google, si todavía no existe.

    El archivo se crea aunque la clave no se ponga, con las instrucciones adentro: un archivo que ya
    existe y explica qué va, se completa cuando uno quiere. Una variable de entorno que nadie
    mencionó, no.
#>
function Crear-Claves {
    if (Test-Path -LiteralPath $Claves) {
        return
    }

    $plantilla = [ordered]@{
        '_leeme' = 'Pegá tu clave de Google entre las comillas de GOOGLE_API_KEY y guardá. No hace falta reiniciar nada más que la aplicación.'
        '_para_que_sirve' = 'Con clave de Google la conversación es hablada y en tiempo real: la escuchás contestar mientras piensa y la podés cortar hablándole encima. Sin clave anda igual, por el camino de siempre: la escuchás cuando terminó de pensar.'
        '_donde_se_saca' = 'https://aistudio.google.com/apikey — es gratis para empezar.'
        'GOOGLE_API_KEY' = ''
        '_apagar_la_sesion_hablada' = 'Con la clave puesta, la sesión hablada arranca encendida. Para usar el camino de siempre teniendo clave, poné VIERNES_LIVE en "false".'
        'VIERNES_LIVE' = ''
        '_openrouter' = 'La clave de OpenRouter NO va acá: vive en las variables de entorno de tu cuenta de Windows, que la puso el instalador. Es más difícil de filtrar por accidente que un archivo.'
        '_seguridad' = 'Este archivo vive fuera del repositorio y nunca se sube a ningún lado. Si compartís tu carpeta de datos con alguien, sacá la clave antes.'
    }

    New-Item -ItemType Directory -Path $Datos -Force | Out-Null
    $plantilla | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Claves -Encoding UTF8
    Listo 'Archivo de claves creado (la de Google es opcional).'
}

<#
    Lee el archivo de claves. Devuelve $null si no está o si alguien lo dejó ilegible.
#>
function Leer-Claves {
    if (-not (Test-Path -LiteralPath $Claves)) {
        return $null
    }

    try {
        $crudo = Get-Content -LiteralPath $Claves -Raw
        if ([string]::IsNullOrWhiteSpace($crudo)) { return $null }
        return $crudo | ConvertFrom-Json
    }
    catch {
        # Un archivo de claves editado a mano y roto no puede frenar la instalación. El mensaje no se
        # muestra: podría venir con un pedazo del archivo adentro, y en ese archivo hay una clave.
        return $null
    }
}

<#
    Pega la clave de Google en el archivo sin tocar el resto.

    Se edita lo que hay en vez de reescribirlo entero porque adentro pueden estar VIERNES_LIVE y las
    notas, y quien reinstala no tiene por qué perderlas. Si el archivo no se puede leer no se lo
    pisa: es del usuario y puede tener otra clave adentro.
#>
function Guardar-Clave-Google([string] $clave) {
    $archivo = Leer-Claves
    if ($null -eq $archivo) {
        Aviso "No pude leer $Claves, así que no lo toco. Pegá la clave a mano en GOOGLE_API_KEY."
        return $false
    }

    if ($archivo.PSObject.Properties.Name -contains 'GOOGLE_API_KEY') {
        $archivo.GOOGLE_API_KEY = $clave
    }
    else {
        $archivo | Add-Member -NotePropertyName 'GOOGLE_API_KEY' -NotePropertyValue $clave
    }

    $archivo | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Claves -Encoding UTF8
    return $true
}

<#
    Pide la clave de Google, que es opcional y por eso se explica antes de pedirla.

    Antes no se pedía: el archivo se creaba con la entrada vacía y las instrucciones adentro, porque
    pedir dos claves en la instalación de algo que todavía no se sabe si va a gustar es una puerta de
    más. Ahora se pide igual —lo pidió quien lo usa—, pero sigue siendo opcional de verdad: Enter y
    se sigue de largo, con el archivo creado y las instrucciones adentro como antes.

    La línea que dice para qué sirve no es decoración. Nadie pega una clave que no sabe qué hace, y
    sin esa línea la pregunta se lee como un requisito más de la instalación.
#>
function Pedir-Clave-Google {
    Crear-Claves

    $puestas = Leer-Claves
    if ($null -ne $puestas -and
        ($puestas.PSObject.Properties.Name -contains 'GOOGLE_API_KEY') -and
        -not [string]::IsNullOrWhiteSpace($puestas.GOOGLE_API_KEY)) {
        # Volver a correr el instalador no puede pisar la clave de nadie en silencio.
        Listo 'Ya tenés una clave de Google configurada.'
        $cambiar = Read-Host '  ¿La reemplazás? (s/N)'
        if ($cambiar -notmatch '^[sS]') { return }
    }

    Titulo 'Tu clave de Google (opcional)'
    Write-Host '  Con clave la escuchás contestar mientras piensa y la podés cortar hablándole' -ForegroundColor DarkGray
    Write-Host '  encima. Sin clave la escuchás cuando terminó de pensar, y todo lo demás anda' -ForegroundColor DarkGray
    Write-Host '  exactamente igual.' -ForegroundColor DarkGray
    Write-Host '  Sacala gratis en https://aistudio.google.com/apikey — o dale Enter y seguimos.' -ForegroundColor DarkGray
    Write-Host ''

    $clave = Leer-Clave-Oculta '  Clave de Google (Enter para dejarla para después)'
    if ([string]::IsNullOrWhiteSpace($clave)) {
        Paso 'Sin clave de Google: anda igual, por el camino de siempre.'
        Paso "La podés pegar cuando quieras en $Claves."
        return
    }

    # A diferencia de la de OpenRouter, acá no se comprueba el prefijo: las claves de Google Cloud
    # empiezan con «AIza» hoy y ese formato no está prometido en ningún lado. Una advertencia que se
    # equivoca enseña a ignorar las advertencias.
    if (Guardar-Clave-Google $clave) {
        $clave = $null
        Listo 'Clave de Google guardada en tu carpeta de datos.'
    }
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
    # Una sola pregunta: ¿este nombre sirve, y en qué queda?
    #
    # No instala nada y no imprime nada más. Existe para que una prueba pueda comprobar que estas
    # reglas y las de AssistantIdentity dicen lo mismo: son las únicas dos que juzgan el mismo
    # nombre, no se pueden compartir —esto corre antes de que la aplicación exista— y si se separan,
    # el instalador acepta nombres que la aplicación rechaza o los guarda con otra forma.
    if ($PSBoundParameters.ContainsKey('ProbarNombre')) {
        $problema = Probar-Nombre $ProbarNombre
        if ($null -eq $problema) {
            Write-Output "ok`t$(Normalizar-Nombre $ProbarNombre)"
        }
        else {
            Write-Output "no`t$problema"
        }

        exit 0
    }

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
        Normalizar-Nombre $Nombre
    } else {
        Pedir-Nombre
    }

    # Las tres preguntas van juntas y antes de las descargas: contestás y te vas. Preguntar algo
    # después de los 500 MB obliga a quedarse mirando una barra para poder contestar.
    if (-not $Nombre) {
        Pedir-Clave
        Pedir-Clave-Google
    }

    Instalar-Aplicacion
    if (-not $SinModelo) {
        Instalar-Modelo
        Instalar-Deteccion
    }

    Titulo 'Últimos detalles'
    Guardar-Nombre $elegido
    Crear-Claves
    Crear-Accesos $elegido
    if (-not $Nombre) { Ofrecer-Arranque }

    Write-Host ''

    # Acá decía «Decile "Hola $nombre" y esperá a que la gota reaccione», y las dos mitades dejaron de
    # ser ciertas: el nombre se reconoce en cualquier parte de la frase y lo dicho antes se recupera
    # de una ventana rodante, así que no hay que saludar ni hay que esperar. La frase vieja enseñaba
    # el gesto equivocado, que es peor que no decir nada: el usuario aprende a hacer una pausa que el
    # programa dejó de necesitar y cree que así funciona.
    Write-Host "   Listo. Nombrala en cualquier parte de la frase y seguí hablando:" -ForegroundColor Green
    Write-Host "   «$elegido, creame una carpeta en el escritorio» — o «creame una carpeta," -ForegroundColor Green
    Write-Host "   $elegido». No hace falta saludar ni esperar." -ForegroundColor Green
    Write-Host ''

    if (Test-Path -LiteralPath $Claves) {
        $tieneGoogle = $false
        try {
            $puestas = Get-Content -LiteralPath $Claves -Raw | ConvertFrom-Json
            $tieneGoogle = -not [string]::IsNullOrWhiteSpace($puestas.GOOGLE_API_KEY)
        }
        catch {
            # Un archivo de claves que alguien editó mal no puede frenar el final de la instalación.
        }

        if (-not $tieneGoogle) {
            Write-Host '   Para que la conversación sea hablada y en tiempo real —que la puedas' -ForegroundColor DarkGray
            Write-Host '   cortar hablándole encima— pegá una clave de Google en:' -ForegroundColor DarkGray
            Write-Host "     $Claves" -ForegroundColor DarkGray
            Write-Host '   Es opcional: sin ella anda igual, por el camino de siempre.' -ForegroundColor DarkGray
            Write-Host ''
        }
    }

    # El conector se nombra y no se da de alta.
    #
    # Darlo de alta escribe en la configuración de Claude del usuario, que es de otra aplicación y no
    # de ésta. Un instalador que toca la configuración de un programa que no instaló es exactamente
    # lo que uno no espera que haga un instalador, por más que el comando sea de una línea.
    $conector = Join-Path $Aplicacion 'viernes-mcp.exe'
    if (Test-Path -LiteralPath $conector) {
        Write-Host "   Y si usás Claude, $elegido se le puede enchufar —ver misiones, dejarle una" -ForegroundColor DarkGray
        Write-Host '   pregunta, mirar tus proyectos— con este comando:' -ForegroundColor DarkGray
        Write-Host ''
        Write-Host "     claude mcp add -s user viernes -- $conector" -ForegroundColor Gray
        Write-Host ''
        Write-Host '   Corrélo vos: da de alta el conector en tu configuración de Claude, que es de' -ForegroundColor DarkGray
        Write-Host '   otra aplicación y no de ésta.' -ForegroundColor DarkGray
        Write-Host ''
    }

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
