# Seguridad y privacidad

Viernes parte de una regla: el modelo propone; el código local decide. Voz, tools, memoria y costos tienen contratos separados para que una respuesta convincente no se convierta en permiso implícito.

## Credenciales

- La única credencial prevista por el MVP es `OPENROUTER_API_KEY`.
- Se lee en tiempo de ejecución desde el entorno del proceso.
- No hay campo de clave en la UI, `appsettings`, `.env`, settings locales, memoria ni argumentos de línea de comandos.
- `ViernesOptions.ToString()` sólo informa `HasApiKey`; nunca incluye el valor.
- El endpoint productivo exige HTTPS. HTTP sólo se permite para loopback en pruebas.
- Sin clave, `OpenRouterChatClient` retorna modo local antes de crear una solicitud de red.

No pegues una clave en un issue, captura, comando compartido, commit, log o reporte. Si se expone una, revocala en el proveedor.

## Micrófono y voz

Hay dos contratos de activación:

- **PTT:** captura sólo mientras el usuario mantiene presionado el núcleo;
- **wake word local:** escucha continua hasta mute/stop, muestra micrófono activo y cede el dispositivo al STT después de detectar una frase.

El detector wake incluido es una **DEMO SAPI no robusta**. Una gramática exacta y un umbral de confianza no eliminan falsos positivos/negativos. La captura de una frase con Whisper también usa un VAD energético simple para cortar silencio; no se presenta como un VAD de producción.

Controles obligatorios ya expresados en la arquitectura:

- indicador unido al orbe cuando el micrófono está abierto;
- mute libera wake/STT y detiene TTS;
- wake y STT nunca toman el dispositivo simultáneamente;
- timeouts de silencio y duración máxima tras activación;
- PTT permanece como fallback;
- ocultar el orbe cancela la frase activa pero **no** apaga la escucha: el micrófono sigue abierto para que Viernes pueda aparecer al ser llamado;
- salir dispone wake, STT y TTS.

### Escucha con el orbe oculto

`ListenWhileHidden` viene activado y mantiene el wake vivo aunque el orbe no esté en pantalla; al
detectar la frase, Viernes se muestra solo. Esto es un cambio deliberado del contrato anterior, en el
que ocultar equivalía a apagar el micrófono.

La consecuencia hay que decirla sin vueltas: **con esta opción activa el micrófono puede estar abierto
sin ningún elemento visible en pantalla**. Los controles que la acotan son:

- **mute sigue siendo el corte duro**: libera el dispositivo, apaga wake y cancela TTS, esté oculto o no;
- el ícono de bandeja expone el estado y permite apagar `Escuchar aunque esté oculto` sin cerrar la app;
- `VIERNES_LISTEN_WHILE_HIDDEN=false` restaura el comportamiento anterior;
- salir de Viernes libera todo.

El audio se sigue procesando localmente y nada de lo capturado antes de la frase se guarda ni se envía.

Whisper y SAPI procesan audio localmente. OpenRouter recibe texto, no audio. Las transcripciones parciales no se guardan en telemetría ni memoria. Un proveedor STT externo futuro necesitará consentimiento y credencial propia; no reutilizará la clave de OpenRouter de manera implícita.

## Herramientas y confirmaciones

- Las tools desconocidas se rechazan.
- Los argumentos JSON se validan y tienen límites de longitud/profundidad.
- `Safe` puede ejecutarse localmente después de validar.
- `RequiresConfirmation` queda pendiente hasta una confirmación explícita.
- `Sensitive` y `Destructive` nunca pasan a ejecución en este MVP, ni aun con `confirmationGranted`.
- Las confirmaciones vencen a los 15 minutos y se limita el número de pendientes/pasos.

No se implementan shell arbitrario, borrado de archivos, elevación, manejo de credenciales, compras, envío de mensajes ni cambios irreversibles. `pc_action` es una vista previa simulada sin `Process.Start`, filesystem o llamadas Win32.

## Datos locales

| Ruta | Datos | No contiene |
|---|---|---|
| `window.json` | coordenadas del orbe | texto, audio, claves |
| `assistant-data.json` | recordatorios y agenda interna | no tiene campos de credencial y rechaza patrones comunes; no ingreses secretos en títulos/notas |
| `settings.json` | preferencias locales normalizadas | campos de tokens o credenciales |
| `memory.json` | hechos breves consentidos | conversaciones, transcripciones, credenciales |
| `usage-ledger.json` | id/fecha/rol/modelo/tokens/costo | prompts, respuestas, audio, claves, argumentos de tools |
| `Models\Whisper\*.bin` | pesos de STT descargados explícitamente | datos del usuario |

Todos viven bajo `%LOCALAPPDATA%\Viernes`. Los stores de datos/settings/memoria escriben mediante archivo temporal y reemplazo. Hay límites de tamaño; la memoria rechaza contenido con forma de conversación y patrones de claves/tokens.

La memoria está pausada para observaciones por defecto. Los comandos del widget permiten guardar un recuerdo explícito, revisar, editar, olvidar y cambiar la pausa. Aprobar una sugerencia y borrar todo siguen disponibles en la API, no como comando UI. Reanudar no inicia extracción automática de conversaciones en este MVP. Nada de esto reentrena el modelo base.

## OpenRouter y costo

Con clave, se envían mensajes de conversación y definiciones/resultados de tools necesarios para el turno. No se envían audio ni archivos de memoria automáticamente.

El cliente limita la respuesta a 4 MiB y la profundidad JSON, no incluye cuerpos remotos en mensajes de error y traduce estados HTTP a texto seguro. Modelo, tokens y costo pueden aparecer en el resultado del turno sin contenido.

`UsageLedger` acumula consumo y puede exigir aprobación al alcanzar límites; valida tamaño, ids y rangos, escribe de forma atómica y retiene datos acotados. El runtime fast lo consulta antes de cada turno y un decorator registra cada completion remota exitosa. El ledger no guarda prompts, respuestas, tool arguments ni secretos.

El widget actual no estima el costo de la próxima petición ni ofrece override: los límites monetarios actúan sobre el consumo conocido. El preflight es un snapshot por turno y un loop de tools puede registrar más de una completion antes del siguiente chequeo. Son guardrails locales, no una garantía contable del proveedor.

## Inicio automático

Es opt-in y por usuario. Viernes escribe únicamente su valor, con la ruta absoluta al ejecutable entre comillas, en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. No pide administrador y se puede desactivar desde la bandeja.

## Conectores futuros

Calendario, correo, navegador, archivos y acciones Windows deberán usar permisos mínimos, consentimiento visible, revocación, scopes acotados y confirmación de efectos sensibles. Autenticar un conector no habilitará automáticamente los demás.
