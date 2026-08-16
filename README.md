# Viernes

Viernes es un asistente personal nativo para Windows: sereno, preciso, cálido, proactivo sin invadir y siempre con el usuario al mando. No es una web app: es un shell WPF que vive sobre el escritorio como un orbe luminoso, se integra con la bandeja y funciona sin una clave de OpenRouter.

> Estado: MVP ejecutable en desarrollo activo. Sin `OPENROUTER_API_KEY` arranca en **modo local seguro** y no intenta conectarse a OpenRouter.

## Primera prueba, sin clave

Requisitos de desarrollo:

- Windows 10 2004 (build 19041) o Windows 11;
- .NET 10 SDK;
- PowerShell 7 recomendado.

Desde la raíz del repositorio:

```powershell
.\scripts\run.ps1
```

La primera vista es un orbe de aproximadamente `78 × 78 px`, sin marco ni panel fijo. Durante escucha, pensamiento, respuesta o confirmación se expande temporalmente a una burbuja breve; después vuelve al orbe. Está siempre disponible, no aparece en la barra de tareas y puede ocultarse o cerrarse desde la bandeja.

Para instalar el STT local preferido antes de ejecutar:

```powershell
.\scripts\setup-whisper.ps1                 # modelo base
.\scripts\setup-whisper.ps1 -Model small    # alternativa más pesada
```

El script descarga un modelo multilingüe desde el repositorio de `whisper.cpp` a `%LOCALAPPDATA%\Viernes\Models\Whisper`, verifica el SHA-1 publicado y nunca toca credenciales. Si Whisper no está disponible, la capa de voz vuelve a SAPI/Windows cuando existe un reconocedor español instalado.

El pipeline Whisper fue verificado de extremo a extremo con audio español sintético, pero la transcripción base todavía tuvo errores. Es una prueba de funcionamiento, no de precisión; ver [validación de voz](docs/VOICE.md#validación-técnica-actual).

También se puede reproducir manualmente el mismo flujo, manteniendo artefactos fuera del workspace:

```powershell
$artifacts = Join-Path $env:LOCALAPPDATA 'Viernes\BuildArtifacts'
dotnet restore .\Viernes.slnx --artifacts-path $artifacts
dotnet build .\Viernes.slnx --no-restore --artifacts-path $artifacts
& "$artifacts\bin\Viernes.App\debug\Viernes.exe"
```

Publicación autocontenida:

```powershell
.\scripts\publish.ps1
```

El ejecutable queda en `%LOCALAPPDATA%\Viernes\Published\win-x64\Viernes.exe`. El script no lee ni escribe claves.

## Interacción del orbe

- **Mover:** arrastrá desde el aura exterior. La posición se guarda en `%LOCALAPPDATA%\Viernes\window.json` y se corrige si queda fuera de las pantallas disponibles.
- **Escribir:** hacé un toque corto sobre el núcleo; aparece una entrada compacta. `Enter` envía.
- **Hablar por PTT:** mantené presionado el núcleo y soltá al terminar. Es el fallback manual y privado.
- **Wake word:** al primer inicio se intenta activar la demo local con `Viernes` y `Hola Viernes`. Admite entre una y ocho frases configurables. El detector incluido usa SAPI con gramática exacta y está marcado **DEMO/no robusto**: puede perder activaciones o activarse por error. No debe confundirse con un wake engine de producción.
- **Privacidad:** el indicador unido al orbe muestra si el micrófono está activo. Mute detiene captura y síntesis; PTT sigue siendo la alternativa si wake word no resulta fiable.
- **Estado breve:** la etiqueta muestra `Escuchando`, `Pensando`, `Hablando` o `Revisar`; la burbuja conserva sólo dos líneas de contexto y desaparece después de unos segundos.
- **Bandeja:** permite mostrar/ocultar, silenciar, encender/apagar la activación por voz demo, habilitar inicio con Windows y salir realmente.
- **Inicio automático:** es opt-in, por usuario y sin elevación; se registra bajo `HKCU\...\Run` sólo al elegirlo.

Detalles y limitaciones de audio: [docs/VOICE.md](docs/VOICE.md).

Opciones de prueba no secretas:

```powershell
$env:VIERNES_WAKE_ENABLED = 'true'                  # false/off/0 lo apaga
$env:VIERNES_WAKE_PHRASES = 'Viernes;Hola Viernes' # hasta 8 frases
$env:VIERNES_STT_PROVIDER = 'sapi'                  # fuerza fallback; omitido prefiere Whisper
$env:VIERNES_WHISPER_MODEL_PATH = "$env:LOCALAPPDATA\Viernes\Models\Whisper\ggml-base.bin"
```

Mute, modo wake/PTT y frases se guardan como preferencias locales sin secretos. Wake mantiene el micrófono abierto mientras está activo; usá el indicador, mute o el toggle de bandeja para apagarlo.

## Qué funciona sin OpenRouter

Un toque corto abre la entrada de texto. Estos comandos forman una superficie local determinista: agenda/recordatorios/búsqueda/PC atraviesan la política de tools; memoria atraviesa su store y política de contenido consentido.

| Comando | Resultado actual |
|---|---|
| `/ayuda` | Muestra la ayuda local. |
| `/recordatorios` | Lista recordatorios guardados en el perfil local. |
| `/recordar 2026-08-17 09:00 \| llamar a Ana` | Crea un recordatorio local. |
| `/agenda` | Lista la agenda local de Viernes. |
| `/evento 2026-08-17 15:30 \| Reunión \| notas` | Crea un evento local; no toca un calendario externo. |
| `/buscar auriculares con cancelación` | Prepara una búsqueda **simulada**; no hace red ni abre el navegador. |
| `/pc open_settings` | Solicita confirmación y luego genera una vista previa **simulada**; no modifica Windows. |
| `/memoria` | Revisa recuerdos locales con tipo e ID corto. |
| `/recordá que prefiero reuniones por la mañana` | Guarda un recuerdo explícito con consentimiento. |
| `/editar memoria ID \| dato corregido` | Edita un elemento de memoria identificado. |
| `/olvidar ID` | Borra un elemento concreto. |
| `/pausar hábitos` / `/reanudar hábitos` | Cambia el permiso local para observaciones temporales. |

Recordatorios y agenda viven en `%LOCALAPPDATA%\Viernes\assistant-data.json`; sus tools rechazan patrones comunes de credenciales antes de persistir, aunque igual no deben usarse para secretos. Las operaciones sensibles o destructivas de PC permanecen bloqueadas incluso después de una confirmación; no hay shell arbitrario, borrado, elevación, compras ni envío de mensajes.

## OpenRouter, sin guardar secretos

La única credencial prevista es `OPENROUTER_API_KEY`. Viernes la lee del entorno del proceso y no ofrece un campo de clave, archivo `.env`, argumento de línea de comandos ni almacenamiento local para ella. Este repositorio se entrega y se prueba sin una clave.

Cuando el usuario decida habilitar OpenRouter fuera de esta prueba, debe crear `OPENROUTER_API_KEY` desde **Editar las variables de entorno de esta cuenta** de Windows y reiniciar Viernes. No pegues la clave en comandos, archivos, logs, capturas, issues ni reportes; la documentación no solicita ni contiene un valor real.

La cartera inicial se puede cambiar sin recompilar:

```powershell
$env:VIERNES_OPENROUTER_FAST_MODEL = 'openai/gpt-5.6-luna'
$env:VIERNES_OPENROUTER_FAST_FALLBACK_MODELS = '~google/gemini-flash-latest'
$env:VIERNES_OPENROUTER_AGENT_MODEL = 'openai/gpt-5.6-terra'
$env:VIERNES_OPENROUTER_REASONING_MODEL = '~anthropic/claude-sonnet-latest'
```

El MVP conversacional usa el rol **fast**. Agent, reasoning, premium, embeddings y resumen local están modelados como selecciones explícitas, pero el widget no escala automáticamente a un modelo más caro. Premium no tiene modelo predeterminado y exige aprobación explícita en su contrato. Al cambiar un modelo hay que validar soporte real de tool calling, español rioplatense, latencia y costo.

Los nombres legacy `VIERNES_OPENROUTER_MODEL` y `VIERNES_OPENROUTER_FALLBACK_MODELS` siguen aceptados, con menor precedencia que las variables `FAST_*`.

Más detalle: [cerebro, modelos y costo](docs/BRAIN.md) y [registro de capacidades](docs/CAPABILITIES.md).

## Presupuestos y costo: estado honesto

La configuración admite límites globales y por rol, máximo de solicitudes, cuota de tareas profundas y una rate card local. El cliente devuelve modelo resuelto, tokens y costo exacto informado por OpenRouter o estimado con una tarifa configurada. `UsageLedger` persiste registros sin contenido en `%LOCALAPPDATA%\Viernes\usage-ledger.json`, calcula totales y el runtime consulta el guard antes de cada turno remoto fast. Cada completion remota exitosa —incluidos pasos posteriores a tools— se registra; si un límite ya fue alcanzado, el siguiente turno no sale.

Límite honesto: el guard fast no conoce de antemano el costo real de una petición, salvo que el llamador proporcione una estimación; el widget actual evalúa con el consumo ya registrado y anota el costo después de completar. Agent/reasoning aún no forman parte del recorrido normal, por lo que la cuota de tareas profundas es una protección preparada para esos flujos.

Variables principales:

```text
VIERNES_OPENROUTER_DAILY_BUDGET_USD
VIERNES_OPENROUTER_MONTHLY_BUDGET_USD
VIERNES_OPENROUTER_MAX_REQUESTS_PER_DAY
VIERNES_MAX_DEEP_TASKS_PER_DAY
VIERNES_OPENROUTER_FAST_DAILY_BUDGET_USD
VIERNES_OPENROUTER_AGENT_DAILY_BUDGET_USD
VIERNES_OPENROUTER_REASONING_DAILY_BUDGET_USD
VIERNES_OPENROUTER_<ROL>_MONTHLY_BUDGET_USD
VIERNES_OPENROUTER_<ROL>_MAX_REQUESTS_PER_DAY
VIERNES_OPENROUTER_RATES_JSON
```

Los precios no están hardcodeados porque cambian. Si el proveedor no informa costo y no se configuró una tarifa, el costo queda desconocido —aunque tokens/requests sí se cuentan— y el presupuesto monetario no puede contabilizarlo; nunca se inventa un valor.

## Memoria personal

`Viernes.Memory` implementa un store JSON local y auditable con tres niveles:

1. **recuerdos explícitos**, guardados porque el usuario lo pidió;
2. **observaciones temporales**, con confianza, fecha y vencimiento; la observación está pausada por defecto;
3. **sugerencias**, que sólo pasan a memoria explícita mediante opt-in.

La API permite revisar, editar, olvidar, borrar todo, aprobar/rechazar sugerencias y pausar/reanudar observaciones. El widget ya conecta revisión, recuerdo explícito, edición, olvido individual y pausa/reanudación mediante los comandos locales de arriba. Rechaza credenciales y contenido con forma de conversación.

No reentrena modelos, no promete “aprendizaje autónomo” y no almacena conversaciones indiscriminadamente. Aunque se reanuden hábitos, el MVP todavía no extrae observaciones automáticamente de una charla ni crea sugerencias por sí solo; el flag sólo deja preparado ese flujo consentido.

## Arquitectura y límites

```text
src/Viernes.App/               Shell WPF, orbe, bandeja y adaptador de runtime
src/Viernes.Core/              Conversación, OpenRouter, tools, riesgo, ledger y presupuestos
src/Viernes.Platform.Windows/  Voz, wake demo, autorun y preferencias locales
src/Viernes.Memory/            Memoria personal local, tipada y consentida
tests/                         Pruebas del núcleo y del store de memoria
docs/                          Arquitectura, voz, seguridad, cerebro y capacidades
scripts/                       Ejecución, publicación e instalación de Whisper
```

- [Arquitectura](docs/ARCHITECTURE.md)
- [Seguridad y privacidad](docs/SECURITY.md)
- [Voz local](docs/VOICE.md)
- [Cerebro, modelos y costo](docs/BRAIN.md)
- [Registro de capacidades](docs/CAPABILITIES.md)

Un modelo no concede permisos: archivos, agenda externa, navegador y acciones Windows sólo existen cuando hay una herramienta o conector concreto, acotado y autorizado.

## Siguiente tramo

1. Validar el recorrido wake→Whisper con voces reales, ruido y español rioplatense, y corregir UX/falsos positivos.
2. Sustituir la demo SAPI por un wake engine local evaluado con ruido, distancia y voces reales.
3. Mostrar totales/costo y aprobación de override en una vista breve, y estimar el costo de la próxima petición antes de enviarla.
4. Agregar aprobación/rechazo de sugerencias y borrado total con confirmación, sin convertir el orbe en un panel permanente.
5. Agregar notificaciones Windows y calendarios externos con OAuth, scopes mínimos y confirmaciones.
6. Empaquetar y firmar la aplicación, y sumar pruebas UI/DPI/múltiples monitores.
