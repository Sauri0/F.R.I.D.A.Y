<#
.SYNOPSIS
    Instala el asistente y lo deja funcionando.

.DESCRIPTION
    Un solo archivo que hace todo: baja la aplicación, baja el modelo de voz, pregunta cómo se va a
    llamar el asistente, guarda sus claves y crea los accesos directos.

    Volver a correrlo actualiza la instalación: se da cuenta solo de que
    ya hay una y pasa a modo actualización. Lo tuyo queda —nombre, claves, historial, memoria,
    preferencias, modelo de voz— y se reemplaza la aplicación entera.

    No vuelve a preguntar lo que ya está; sí pregunta lo que falte, que es ganar algo y no
    repreguntar. «Sin volver a preguntar nada» decía este párrafo, y era falso.

.NOTES
    Las claves son del usuario y ninguna viaja acá adentro. Las dos se piden en el momento, se leen
    como texto oculto, no se muestran, no se registran y no se escriben en ningún archivo del
    proyecto. La de OpenRouter va a las variables de entorno de la cuenta de Windows; la de Google
    —que es opcional— al archivo de claves de la carpeta de datos, porque así lo pidió quien lo usa:
    abrir, pegar y guardar es más simple que aprender setx.
#>

[CmdletBinding()]
param(
    # Fuerza el modo actualización. Para el acceso directo «Actualizar». Sin esto igual se detecta
    # sola una instalación previa: el modificador sólo saltea la detección.
    [switch] $Actualizar,

    # Cambia el nombre y las claves sin reinstalar nada.
    [switch] $Reconfigurar,

    # Instala sin preguntar, con estos valores. Para pruebas automatizadas.
    [string] $Nombre,
    [switch] $SinModelo,

    # Comprueba un nombre y sale sin instalar nada. Ver el bloque principal para el porqué.
    [string] $ProbarNombre,

    # Informa qué encontró instalado y sale sin tocar nada. Para probar la detección.
    [switch] $ProbarDeteccion,

    # Sólo para pruebas: mira otra carpeta en vez de %LOCALAPPDATA%\Viernes. La aplicación NO lee de
    # acá, así que una instalación hecha con esto no arranca. Existe para poder probar la detección
    # contra carpetas armadas a mano sin tocar la instalación real de quien corre las pruebas.
    [string] $CarpetaDeDatos
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
$Datos = if ([string]::IsNullOrWhiteSpace($CarpetaDeDatos)) {
    Join-Path $env:LOCALAPPDATA 'Viernes'
} else {
    $CarpetaDeDatos
}

$Aplicacion = Join-Path $Datos 'app'
$Modelos = Join-Path $Datos 'Models\Whisper'
$Deteccion = Join-Path $Datos 'Models\Vad'
$Claves = Join-Path $Datos 'claves.json'
# Las dos llevan «Ruta» adelante porque «$Version» y «$Preferencias» chocaban con los «$version» y
# «$preferencias» locales de las funciones: PowerShell no distingue mayúsculas en los nombres de
# variable, así que la local tapaba a la de arriba y Test-Path recibía un diccionario vacío.
$RutaPreferencias = Join-Path $Datos 'settings.json'
$RutaVersion = Join-Path $Datos 'version.txt'

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
    que lo que las mantiene juntas es una prueba: ReglasDelNombreTests corre este mismo archivo con
    -ProbarNombre y compara veredicto y forma final contra AssistantIdentity, nombre por nombre.
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

<#
    El nombre por defecto es parámetro porque al reconfigurar tiene que ser el que ya tiene.

    Si acá quedara fijo en «Viernes», quien entra a cambiar sus claves y le da Enter a la pregunta
    del nombre le estaría renombrando el asistente sin querer.
#>
function Pedir-Nombre([string] $porDefecto = 'Viernes') {
    Titulo '¿Cómo querés que se llame?'
    Write-Host '  Es el nombre con el que lo vas a despertar. Podés cambiarlo después.' -ForegroundColor DarkGray
    Write-Host ''

    while ($true) {
        $respuesta = Read-Host "  Nombre (Enter para «$porDefecto»)"
        if ([string]::IsNullOrWhiteSpace($respuesta)) { $respuesta = $porDefecto }

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
        #
        # EL NOMBRE DE LA VARIABLE. Acá decía VIERNES_DAILY_BUDGET, que no la lee nadie: el guardián
        # lee ViernesOptions.DailyBudgetEnvironmentVariable, que vale
        # VIERNES_OPENROUTER_DAILY_BUDGET_USD. O sea que el tope se anunciaba en pantalla, se
        # escribía en la cuenta de Windows, y no frenaba absolutamente nada. El comentario de acá
        # arriba —«existe el aparato entero y no frena nunca»— describía el síntoma exacto y culpaba
        # a la falta de configuración, cuando el instalador venía configurando la variable
        # equivocada. Todas las instalaciones hechas hasta hoy quedaron sin tope.
        $Tope = 'VIERNES_OPENROUTER_DAILY_BUDGET_USD'
        if (-not [Environment]::GetEnvironmentVariable($Tope, 'User')) {
            [Environment]::SetEnvironmentVariable($Tope, '2', 'User')
            Set-Item -LiteralPath "Env:$Tope" -Value '2'
            Paso "Tope de gasto: USD 2 por día. Se cambia con la variable $Tope."
        }

        # Y se limpia el nombre viejo si quedó de una instalación anterior, para que nadie lo
        # encuentre y crea que sirve de algo.
        if ([Environment]::GetEnvironmentVariable('VIERNES_DAILY_BUDGET', 'User')) {
            [Environment]::SetEnvironmentVariable('VIERNES_DAILY_BUDGET', $null, 'User')
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
        $respuesta = if ($null -ne $_.Exception.PSObject.Properties['Response']) {
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

    if ((Test-Path -LiteralPath $RutaVersion) -and
        ((Get-Content -LiteralPath $RutaVersion -Raw).Trim() -eq $release.tag_name) -and
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
    Set-Content -LiteralPath $RutaVersion -Value $release.tag_name -NoNewline
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

    Write-Host '  Escucha en tu máquina, sin mandar audio a ningún lado. Son 465 MB' -ForegroundColor DarkGray
    Write-Host '  y se bajan una sola vez.' -ForegroundColor DarkGray

    New-Item -ItemType Directory -Path $Modelos -Force | Out-Null
    Bajar-Archivo $ModeloUrl $destino 'el modelo de voz (465 MB)'

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

<#
    Lee las preferencias tal cual están, campo por campo, sin interpretar ninguno.

    Acá había un «ConvertFrom-Json -AsHashtable», que no existe en Windows PowerShell 5.1 —llegó en
    la 6—. Como la llamada estaba dentro de un try/catch que devolvía un diccionario vacío, en 5.1
    fallaba en silencio y la reinstalación pisaba settings.json entero: se perdían la forma del orbe,
    la posición del orbe, el micrófono silenciado y el modelo elegido. Justo lo que una actualización
    no puede hacer. Y 5.1 es el caso normal, porque INSTALAR.cmd cae ahí cuando no hay pwsh.

    Devuelve un diccionario ordenado para conservar el orden del archivo: así una actualización que
    no cambia nada tampoco reordena el archivo del usuario.
#>
function Leer-Preferencias {
    $preferencias = [ordered]@{}
    if (-not (Test-Path -LiteralPath $RutaPreferencias)) { return $preferencias }

    try {
        $crudo = Get-Content -LiteralPath $RutaPreferencias -Raw
        if ([string]::IsNullOrWhiteSpace($crudo)) { return $preferencias }

        $objeto = $crudo | ConvertFrom-Json
        foreach ($campo in $objeto.PSObject.Properties) {
            $preferencias[$campo.Name] = $campo.Value
        }
    }
    catch {
        # Un settings.json roto a mano no puede frenar la instalación. Se arranca de cero, que es lo
        # mismo que va a hacer la aplicación cuando no lo pueda leer.
        return [ordered]@{}
    }

    return $preferencias
}

<#
    Devuelve el nombre guardado, o $null si no hay ninguno.

    Es la señal de «esta instalación ya está configurada»: sin nombre no hubo nunca una instalación
    terminada, porque el nombre se escribe al final y es lo primero que se pregunta.
#>
function Leer-Nombre {
    $preferencias = Leer-Preferencias
    if (-not $preferencias.Contains('assistantName')) { return $null }

    $nombre = $preferencias['assistantName']
    if ($null -eq $nombre -or [string]::IsNullOrWhiteSpace([string] $nombre)) { return $null }

    return [string] $nombre
}

function Guardar-Nombre([string] $nombre) {
    New-Item -ItemType Directory -Path $Datos -Force | Out-Null

    # Se respeta lo que ya había: quien reinstala no pierde su forma de orbe ni su micrófono
    # silenciado sólo porque cambió el nombre.
    $preferencias = Leer-Preferencias

    $preferencias['schemaVersion'] = 1
    $preferencias['assistantName'] = $nombre

    # Las frases se borran a propósito: se derivan del nombre. Dejarlas escritas congelaría las del
    # nombre anterior y el asistente seguiría respondiendo al viejo, o a ninguno.
    $preferencias.Remove('wakeWordPhrases')

    $preferencias | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $RutaPreferencias -Encoding UTF8
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

    if ($null -ne $archivo.PSObject.Properties['GOOGLE_API_KEY']) {
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
        ($null -ne $puestas.PSObject.Properties['GOOGLE_API_KEY']) -and
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

<#
    Reapunta el arranque automático si ya estaba activado.

    No lo activa ni lo desactiva: eso lo eligió el usuario y no es asunto de una actualización. Lo que
    corrige es la ruta, porque una entrada escrita por una versión vieja puede apuntar a una carpeta
    que esta versión ya no usa, y entonces el arranque automático deja de arrancar nada sin que nadie
    se entere hasta el próximo reinicio.
#>
function Sincronizar-Arranque {
    <#
        Contesta si el arranque con Windows YA estaba configurado, y de paso le corrige la ruta.

        Devuelve un booleano porque el camino de actualizacion necesita distinguir dos cosas que no
        son lo mismo: "lo tenia y quedo bien" de "nunca lo tuvo". En el segundo caso hay que
        ofrecerselo —una instalacion vieja puede ser anterior a que la opcion existiera, y quien
        nunca la vio no se entera de que esta—. Antes esta funcion no devolvia nada y el camino de
        actualizacion no preguntaba nunca.
    #>
    $clave = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $entrada = Get-ItemProperty -Path $clave -Name 'Viernes' -ErrorAction SilentlyContinue
    if ($null -eq $entrada) { return $false }

    $esperado = "`"$(Join-Path $Aplicacion 'Viernes.exe')`""
    if ($entrada.Viernes -eq $esperado) { return $true }

    Set-ItemProperty -Path $clave -Name 'Viernes' -Value $esperado
    Paso 'El arranque con Windows apuntaba a otro lado. Corregido.'
    return $true
}

<#
    Cierra la aplicación y espera de verdad a que muera.

    Acá había un Stop-Process seguido de 600 ms de sueño, y esos 600 ms eran una apuesta: si el
    proceso tardaba más —un equipo cargado, un disco lento, un modelo de voz todavía en memoria—, el
    Remove-Item que viene después fallaba con «el archivo está en uso» y la actualización se caía a
    mitad, con la carpeta nueva ya extraída al lado. Ahora se espera hasta que el proceso no esté, y
    si no se muere se dice por qué en vez de romper contra un archivo tomado.

    Antes de matarla se le pide que cierre, y no es por cortesía: Window_Closing guarda dónde quedó
    el orbe y recién después cancela el cierre para esconderse en la bandeja. O sea que el pedido no
    la cierra nunca —está hecho así a propósito—, pero sí hace que se guarde el último lugar del
    orbe. Sin esa línea, actualizar le mueve el orbe a quien lo tenía donde quería. Por eso se espera
    un ratito corto y se pasa a matar, en vez de esperar un cierre que no va a llegar.
#>
function Detener-Aplicacion {
    $procesos = @(Get-Process -Name 'Viernes' -ErrorAction SilentlyContinue)
    if ($procesos.Count -eq 0) { return }

    Paso 'Cerrando la aplicación, que está abierta...'

    foreach ($proceso in $procesos) {
        try { [void] $proceso.CloseMainWindow() } catch { }
    }

    Start-Sleep -Milliseconds 800

    foreach ($proceso in $procesos) {
        try {
            if (-not $proceso.HasExited) {
                $proceso | Stop-Process -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            # Un proceso que se murió entre la comprobación y el intento no es un problema: el
            # objetivo era exactamente ése.
        }
    }

    # Windows libera los archivos un instante después de que el proceso desaparece de la lista, así
    # que no alcanza con que Get-Process no lo vea: hay que darle ese instante o el reemplazo falla.
    $limite = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $limite) {
        if (@(Get-Process -Name 'Viernes' -ErrorAction SilentlyContinue).Count -eq 0) {
            Start-Sleep -Milliseconds 400
            return
        }

        Start-Sleep -Milliseconds 200
    }

    throw 'No pude cerrar la aplicación. Cerrala vos desde el ícono de la bandeja y volvé a correr esto.'
}

#endregion

#region qué hay instalado

<#
    Mira el disco y decide si acá ya vive una instalación.

    Que exista la carpeta de datos NO alcanza, y por eso no se la mira: el instalador la crea —con
    claves.json adentro— antes de bajar un solo byte, así que una corrida que se cortó en la descarga
    deja la carpeta hecha y nada más. Tratar eso como «ya instalado» sería saltear las preguntas de
    alguien que nunca las contestó.

    Las tres señales que sí cuentan son las que sólo aparecen cuando algo terminó:
      · version.txt        lo escribe Instalar-Aplicacion recién después de mover la carpeta nueva
                           en su lugar, o sea cuando la aplicación quedó entera;
      · app\Viernes.exe    el binario, que es lo único que se reemplaza;
      · assistantName      lo escribe el paso final de la instalación, y es lo primero que se pregunta.

    Las tres presentes es una instalación terminada. Ninguna, una instalación nueva. Algunas es una
    corrida que se cortó por la mitad, y se dice: ahí no hay que preguntar todo de nuevo, pero
    tampoco se puede dar por sentado que está lo que falta.

    El modelo de voz y el detector quedan aparte a propósito. No dicen si hay instalación —se puede
    instalar sin ellos con -SinModelo, y una versión nueva puede necesitar uno que antes no hacía
    falta—: son equipo que se completa siempre, en la instalación y en la actualización.
#>
function Ver-Instalacion {
    $version = $null
    if (Test-Path -LiteralPath $RutaVersion) {
        try {
            $leido = (Get-Content -LiteralPath $RutaVersion -Raw).Trim()
            if (-not [string]::IsNullOrWhiteSpace($leido)) { $version = $leido }
        }
        catch {
            # Un version.txt ilegible cuenta como que no está: se va a reescribir igual.
        }
    }

    $ejecutable = Test-Path -LiteralPath (Join-Path $Aplicacion 'Viernes.exe')
    $nombre = Leer-Nombre

    $modelo = Join-Path $Modelos $ModeloNombre
    $tieneModelo = (Test-Path -LiteralPath $modelo) -and
        ((Get-Item -LiteralPath $modelo).Length -ge $ModeloMinimoBytes)

    $silero = Join-Path $Deteccion 'silero_vad.onnx'
    $tieneDetector = (Test-Path -LiteralPath $silero) -and
        ((Get-Item -LiteralPath $silero).Length -ge $SileroMinimoBytes) -and
        (Test-Path -LiteralPath (Join-Path $Deteccion 'Microsoft.ML.OnnxRuntime.dll')) -and
        (Test-Path -LiteralPath (Join-Path $Deteccion 'onnxruntime.dll')) -and
        (Test-Path -LiteralPath (Join-Path $Deteccion 'onnxruntime_providers_shared.dll'))

    $puestas = Leer-Claves
    $tieneGoogle = $null -ne $puestas -and
        ($null -ne $puestas.PSObject.Properties['GOOGLE_API_KEY']) -and
        -not [string]::IsNullOrWhiteSpace($puestas.GOOGLE_API_KEY)

    $tieneOpenRouter = -not [string]::IsNullOrWhiteSpace(
        [Environment]::GetEnvironmentVariable('OPENROUTER_API_KEY', 'User'))

    $senales = 0
    if ($null -ne $version) { $senales++ }
    if ($ejecutable) { $senales++ }
    if ($null -ne $nombre) { $senales++ }

    $estado = if ($senales -eq 0) { 'nueva' }
        elseif ($senales -eq 3) { 'completa' }
        else { 'a-medias' }

    return [ordered]@{
        Estado = $estado
        Version = $version
        TieneEjecutable = $ejecutable
        Nombre = $nombre
        TieneOpenRouter = $tieneOpenRouter
        TieneGoogle = $tieneGoogle
        TieneModelo = $tieneModelo
        TieneDetector = $tieneDetector
    }
}

<#
    Dice qué se conserva y qué se reemplaza, antes de tocar nada.

    Es corto a propósito: quien actualiza necesita una respuesta —«¿pierdo lo mío?»— y no un párrafo.
    Lo que lo hace cierto es dónde viven las cosas: todo lo del usuario está en la carpeta de datos, y
    lo único que la actualización borra de ahí adentro es la subcarpeta app.
#>
function Contar-Que-Pasa($estado) {
    # Cada linea sale de lo que Ver-Instalacion encontro DE VERDAD, y no de lo que suele haber.
    # Prometer «se conserva el nombre y las claves» sobre una instalacion que no tiene ninguna de las
    # dos es la clase de mensaje que hace desconfiar de todo el resto de la pantalla, y encima justo
    # en el momento en que el usuario esta decidiendo si dejar que le toquen la instalacion.
    Write-Host ''
    Write-Host '  Se conserva:' -ForegroundColor Gray

    if ($null -ne $estado.Nombre) {
        Write-Host "    · el nombre que elegiste: $($estado.Nombre)" -ForegroundColor DarkGray
    }

    $claves = @()
    if ($estado.TieneOpenRouter) { $claves += 'la de OpenRouter' }
    if ($estado.TieneGoogle) { $claves += 'la de Google' }
    if ($claves.Count -gt 0) {
        Write-Host "    · tus claves: $($claves -join ' y ')" -ForegroundColor DarkGray
    }

    Write-Host '    · el historial, la memoria y tus preferencias' -ForegroundColor DarkGray
    Write-Host '    · donde dejaste el orbe y que forma le diste' -ForegroundColor DarkGray

    if ($estado.TieneModelo) {
        Write-Host '    · el modelo de voz y el detector, que no se vuelven a bajar' -ForegroundColor DarkGray
    }

    Write-Host '  Se reemplaza:' -ForegroundColor Gray
    Write-Host '    · la aplicacion entera: lo visual, las funciones, todo' -ForegroundColor DarkGray

    # Y lo que falta se dice tambien, porque enterarse despues de una descarga de 200 MB de que
    # todavia hay que contestar algo es exactamente lo que este bloque existe para evitar.
    $faltan = @()
    if ($null -eq $estado.Nombre) { $faltan += 'el nombre' }
    if (-not $estado.TieneOpenRouter) { $faltan += 'la clave de OpenRouter' }
    if (-not $estado.TieneModelo) { $faltan += 'el modelo de voz' }
    if ($faltan.Count -gt 0) {
        Write-Host '  Falta, y te lo pregunto ahora:' -ForegroundColor Gray
        Write-Host "    · $($faltan -join ', ')" -ForegroundColor DarkGray
    }

    Write-Host ''
}

<#
    Completa lo que falta y no vuelve a preguntar lo que ya está.

    La diferencia importa: repreguntar el nombre y las claves en cada actualización es exactamente lo
    que hace que uno deje de actualizar. Pero callarse que falta la clave de Google es perder algo que
    se podía ganar en una línea, así que eso sí se ofrece.
#>
function Completar-Lo-Que-Falta($estado) {
    if ($null -eq $estado.Nombre) {
        Aviso 'La instalación anterior quedó sin nombre guardado. Lo elegís ahora.'
        $elegido = Pedir-Nombre
        Guardar-Nombre $elegido
        return $elegido
    }

    Paso "Sigue llamándose $($estado.Nombre). Se cambia con -Reconfigurar."
    return $estado.Nombre
}

#endregion

#region los caminos

<#
    Actualiza una instalación que ya existe.

    Reemplaza la aplicación y deja el equipo completo, que son dos cosas distintas: antes acá corría
    sólo Instalar-Aplicacion, así que una versión nueva que necesitara el detector de voz —o el
    modelo, en un equipo instalado con -SinModelo— quedaba con el binario nuevo y sin con qué
    escuchar, y los accesos directos seguían siendo los que hubiera dejado la versión anterior.
#>
function Actualizar-Instalacion($estado) {
    Titulo 'Actualizar'

    if ($estado.Estado -eq 'a-medias') {
        Aviso 'La instalacion anterior quedo a medias. Completo lo que falte.'
    }
    elseif ($null -ne $estado.Version) {
        Paso "Tenes la version $($estado.Version)."
    }

    Contar-Que-Pasa $estado

    # PRIMERO SE PREGUNTA Y DESPUES SE BAJA, y no al reves.
    #
    # Acá el orden era Instalar-Aplicacion, el modelo, el detector, y recien despues completar lo que
    # faltara. O sea que sobre una instalacion a medias sin nombre, o sin la clave de OpenRouter, el
    # usuario tenia que quedarse mirando 200 MB de barra para poder contestar. Es exactamente lo que
    # el comentario del camino de instalacion limpia dice que no hay que hacer, en el mismo archivo.
    #
    # Y antes de escribir settings.json hay que cerrar la aplicacion. Si esta abierta —el caso
    # normal: arranca con Windows y vive en la bandeja— conserva su copia en memoria de las
    # preferencias y reescribe el archivo ENTERO en cuanto tocas el mute, la forma del orbe o
    # cualquier otra cosa del menu, arrastrando el nombre viejo. El renombre se perderia sin aviso.
    Detener-Aplicacion

    Crear-Claves
    $elegido = Completar-Lo-Que-Falta $estado

    # Solo se piden las claves que faltan. La que ya esta no se toca ni se menciona con su valor.
    if (-not $estado.TieneOpenRouter) {
        Aviso 'No tenes clave de OpenRouter puesta. Sin ella el asistente arranca pero no piensa.'
        Pedir-Clave
    }
    else {
        Paso 'Tu clave de OpenRouter esta puesta.'
    }

    if (-not $estado.TieneGoogle) {
        Pedir-Clave-Google
    }
    else {
        Paso 'Tu clave de Google esta puesta.'
    }

    Instalar-Aplicacion
    if (-not $SinModelo) {
        Instalar-Modelo
        Instalar-Deteccion
    }

    Titulo 'Ultimos detalles'
    Crear-Accesos $elegido

    # Si ya tenia arranque automatico, se le corrige la ruta. Si NUNCA lo tuvo, se le ofrece: una
    # instalacion vieja puede ser anterior a que esto existiera, y no preguntar nunca significa que
    # esa gente no se entera de que la opcion existe.
    if (-not (Sincronizar-Arranque)) {
        Ofrecer-Arranque
    }

    Write-Host ''
    Listo "Actualizado. $elegido quedo como estaba, con la aplicacion nueva."
    Write-Host "   Para cambiarle el nombre desde la aplicacion: clic derecho en el orbe." -ForegroundColor DarkGray
    Write-Host "   Para cambiarlo o cambiar las claves desde aca: INSTALAR.cmd -Reconfigurar" -ForegroundColor DarkGray
    Write-Host ''
}

<#
    Cambia el nombre y las claves sin bajar ni reemplazar nada.

    Existe porque hasta ahora la única forma de cambiar el nombre desde afuera de la aplicación era
    reinstalar, y reinstalar para cambiar una palabra son 500 MB y una espera. Acá no se toca el
    disco más allá de settings.json, claves.json y los accesos directos, que se rehacen porque llevan
    el nombre.
#>
function Reconfigurar-Instalacion($estado) {
    Titulo 'Reconfigurar'

    if ($estado.Estado -eq 'nueva') {
        throw 'Aca no hay nada instalado todavia. Corre el instalador sin -Reconfigurar.'
    }

    $actual = if ($null -ne $estado.Nombre) { $estado.Nombre } else { 'Viernes' }
    Write-Host "  Hoy se llama $actual. No se baja ni se reemplaza nada." -ForegroundColor DarkGray

    # Se cierra ANTES de escribir settings.json, no despues.
    #
    # La aplicacion abierta tiene su propia copia de las preferencias en memoria y reescribe el
    # archivo entero —con el nombre viejo adentro— en cuanto tocas el mute, la forma del orbe, seguir
    # el monitor activo, oir oculto o la activacion por voz: todo eso esta en el menu de la bandeja.
    # Asi que renombrabas por aca, tocabas cualquiera de esos, y el nombre volvia solo al anterior
    # sin decir nada. Avisar "cerralo y volve a abrirlo" al final no cubria ese caso: para cuando el
    # usuario lee el aviso, la aplicacion ya puede haber pisado el archivo.
    Detener-Aplicacion

    $elegido = Pedir-Nombre $actual
    Guardar-Nombre $elegido

    Pedir-Clave
    Pedir-Clave-Google

    if (Test-Path -LiteralPath (Join-Path $Aplicacion 'Viernes.exe')) {
        Crear-Accesos $elegido
    }

    Write-Host ''
    Listo "Listo. Ahora se llama $elegido."
    if ($elegido -ne $actual) {
        Paso "Lo despertas diciendo «Hola $elegido», «Che $elegido» o «Ey $elegido»."
        Paso 'Tambien se le puede cambiar el nombre desde la aplicacion: clic derecho en el orbe.'
    }

    Write-Host ''
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

    # Qué encontró y nada más. Ninguna clave se imprime, ni entera ni en pedazos: sólo si está.
    if ($ProbarDeteccion) {
        $visto = Ver-Instalacion
        foreach ($campo in $visto.Keys) {
            $valor = $visto[$campo]
            if ($null -eq $valor) { $valor = '' }
            Write-Output "$campo`t$valor"
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

    $instalado = Ver-Instalacion

    if ($Reconfigurar) {
        Reconfigurar-Instalacion $instalado
        exit 0
    }

    <#
        La detección va acá, antes de la primera pregunta.

        Sin esto, correr el instalador en un equipo que ya tiene el asistente vuelve a preguntar el
        nombre y las dos claves, y quien contesta ya las contestó. -Actualizar sigue existiendo para
        el acceso directo, pero ahora es un atajo y no la única puerta: el camino normal —doble clic
        en INSTALAR.cmd— llega solo a la actualización.

        -Nombre queda afuera a propósito: lo usan las pruebas para instalar sin preguntar, y ahí lo
        que se quiere es la instalación completa aunque haya restos de una anterior.
    #>
    if (($Actualizar -or $instalado.Estado -ne 'nueva') -and -not $Nombre) {
        if ($instalado.Estado -eq 'nueva') {
            Aviso 'No encontré una instalación previa. Instalo desde cero.'
        }
        else {
            Actualizar-Instalacion $instalado
            exit 0
        }
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
    if (-not $Nombre -and -not $ProbarDeteccion) { Read-Host '  Enter para cerrar' | Out-Null }
    exit 1
}

#endregion
