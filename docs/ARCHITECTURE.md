# Arquitectura de Viernes

Viernes separa presentación, voz/plataforma, conversación, capacidades y memoria. La interfaz WPF no conoce el protocolo JSON de OpenRouter; un modelo no llama a Windows directamente; y ningún store local acepta credenciales.

Lo que sigue describe el código que hay. La versión anterior de este documento afirmaba que `pc_action` «nunca llama a `Process.Start`, shell, filesystem ni Win32» y que sus acciones permitidas eran simulaciones: hoy hay un ejecutor real de Windows, una herramienta de PowerShell y otra de archivos. Se corrigió acá por la misma razón por la que se corrigió en `SECURITY.md`: un diagrama que describe el sistema anterior manda a revisar el lugar equivocado.

```text
Viernes.App (WPF, proceso de escritorio)
  ├─ MainWindow + MainViewModel
  │    ├─ orbe 78×78 en reposo
  │    ├─ burbuja temporal 344×112 / entrada 352×158
  │    ├─ estados, drag, confirmación y privacidad
  │    └─ bandeja, visibilidad y ciclo de vida
  ├─ AssistantRuntime
  │    ├─ adapta eventos de voz/estado al Dispatcher WPF
  │    ├─ comandos locales deterministas
  │    ├─ conversación abierta, confirmaciones y freno de emergencia
  │    ├─ avisos de recordatorios y de agenda
  │    └─ destilación de memoria al cerrar una charla
  ├─ Viernes.Platform.Windows
  │    ├─ Whisper local preferido / SAPI fallback
  │    ├─ wake-word SAPI DEMO + handoff de micrófono
  │    ├─ TTS SAPI local (respaldo de la voz neural)
  │    ├─ ejecutor real de acciones de Windows y captura de pantalla
  │    ├─ autorun HKCU por usuario
  │    └─ preferencias locales sin secretos
  ├─ Viernes.Core
  │    ├─ cliente OpenRouter, tools y fallback fast
  │    ├─ cartera de modelos seleccionada por rol
  │    ├─ tokens/costo por completion, ledger y presupuestos
  │    ├─ herramientas concretas (PC, archivos, PowerShell, agenda…)
  │    ├─ reglas, objetivos, misiones y permisos persistidos
  │    ├─ cliente MCP: herramientas de terceros por la misma puerta
  │    └─ política local de riesgo
  └─ Viernes.Memory
       └─ store local de memoria consentida + comandos explícitos
```

## Shell compacto

La ventana es nativa, transparente, sin marco, topmost y fuera de la barra de tareas. En reposo sólo renderiza el orbe; no mantiene un panel. Una interacción abre una burbuja con estado/transcripción/respuesta breve y vuelve al modo mínimo siete segundos después de presentar un resultado. Las confirmaciones y la entrada escrita son expansiones explícitas, no superficies permanentes.

El área exterior del orbe ejecuta `DragMove`. Al ocultarse o cerrarse, la posición se guarda en `%LOCALAPPDATA%\Viernes\window.json`. En el próximo inicio se valida contra todas las áreas de trabajo y se usa una posición segura si el punto quedó fuera de pantalla.

`NotifyIcon` mantiene mostrar/ocultar, mute, wake demo, escuchar oculto, autorun, forma del orbe y salida. El proceso es de instancia única. Ocultar la ventana **no** apaga la escucha —para eso está mute—; `Salir de Viernes` dispone voz, bandeja, servidores MCP y runtime.

`Ctrl+Shift+Alt+J` es el freno: cancela el turno en curso —incluido el bucle de herramientas y cualquier comando de PowerShell—, corta la voz, cierra la conversación y silencia el micrófono. No pasa por el modelo ni por la política.

## Modos de diagnóstico

`Viernes.exe` acepta `--check-voice`, `--check-listen`, `--check-whisper`, `--check-mic` y `--render-orb`, que corren un informe y terminan el proceso sin levantar la interfaz. Como el proyecto es `WinExe`, un proceso de subsistema GUI arranca sin salida estándar y esos informes no se veían en ningún lado: `ConsoleBridge` engancha la consola del proceso padre con `AttachConsole(-1)` desde un inicializador de módulo —antes de que .NET construya `Console.Out`, que es la única ventana de tiempo en la que sirve— y sólo cuando el proceso no nació ya con una salida redirigida.

## Flujos de entrada

### Texto y modo local

1. Un toque corto abre la entrada compacta.
2. `LocalCommandRouter` intenta primero la sintaxis local documentada.
3. Agenda/recordatorios/búsqueda/PC se convierten en tool calls tipados y atraviesan `ToolExecutor` + `SafeToolPolicy`; los comandos de memoria llaman al store local consentido.
4. Si no es un comando local, `ConversationOrchestrator` procesa el turno.
5. Sin `OPENROUTER_API_KEY`, devuelve modo local sin enviar una solicitud de red.
6. Con clave, usa el perfil fast y expone las herramientas al modelo; la política sigue decidiendo localmente.

### PTT

1. Mantener presionado el núcleo abre el proveedor de reconocimiento.
2. Soltar finaliza la captura y produce texto.
3. El audio no entra al historial conversacional ni se envía a OpenRouter.
4. El texto reconocido sigue el mismo flujo que la entrada escrita.

### Wake word y conversación abierta

```text
SAPI wake DEMO ──detección──> detener wake y liberar micrófono
                                      │
                                      v
                            se abre la conversación
                                      │
                     ┌────────────────┴────────────────┐
                     │  captura acotada de una frase   │
                     │  Whisper / SAPI STT             │
                     │  texto → Core/tools → respuesta │
                     └────────────────┬────────────────┘
                                      │ vuelve a escuchar
                        despedida, silencio largo,
                        mute o la tool «descansar»
                                      │
                                      v
                        destilación de memoria y
                        reanudar wake si correspondía
```

`WakeWordRecognitionCoordinator` garantiza que wake y STT no usen el dispositivo al mismo tiempo. Dentro de la conversación el micrófono es del bucle y el wake queda pausado; el bucle espera a que no quede voz sonando antes de abrir la captura, porque si no Viernes se escucha a sí misma y se contesta sola. La frase tiene timeout de silencio inicial, silencio final y duración máxima. En Whisper el corte usa un VAD energético simple: ayuda a acotar la captura, pero también es una demo sensible al ruido.

Cerrar la conversación —por cualquiera de sus caminos— vacía los turnos acumulados. Los dos caminos que destilan memoria se los llevan antes de cerrar; los demás los descartan, para que lo dicho en una charla no aparezca en lo que se aprende de la siguiente.

## Cerebro por roles

`ModelPortfolio` contiene modelos por rol y sólo devuelve el rol solicitado por el llamador:

- fast: conversación diaria y tools simples;
- agent/planning: varios pasos;
- reasoning: análisis difícil/documentos;
- premium: requiere modelo configurado y `PremiumApproved`;
- embeddings/resumen: prefiere ejecución local y requiere opt-in para remoto.

El cliente normal de `ConversationOrchestrator` selecciona **fast**. Los otros perfiles son contratos utilizables por flujos explícitos; no existe un router oculto que suba de costo por sí solo. Los fallbacks configurados se aplican actualmente al lane fast.

Cada respuesta remota retorna modelo, tokens de entrada/salida y costo exacto del proveedor o una estimación desde una rate card configurada. `AssistantRuntime` consulta `UsageLedger` antes de un turno fast y evita iniciarlo si el guard no permite continuar; cuando el guard corta, ofrece una autorización de gasto que vive sólo en memoria y sólo por hoy. Un decorator registra cada completion remota exitosa, también las iteraciones posteriores a tools. El ledger calcula totales diarios/mensuales/globales/por rol sin contenido. El preflight es un snapshot por turno, no reserva presupuesto y no aporta una estimación previa; un turno de varias completions puede cruzar el límite y el siguiente quedará bloqueado.

## Herramientas y permisos

`IAssistantTool` aporta nombre, descripción, esquema JSON, riesgo y ejecutor. `SafeToolPolicy` toma la decisión independientemente del LLM:

- `Safe`: se puede ejecutar tras validar argumentos;
- `RequiresConfirmation`: se ejecuta sólo después de una confirmación vigente;
- `Sensitive` y `Destructive`: permanecen pendientes/bloqueadas, incluso si se confirman;
- tool desconocida o argumentos inválidos: rechazo.

La confirmación expira a los 15 minutos y el núcleo limita el número de iteraciones de tools. El riesgo puede depender de los argumentos: `pc_action` clasifica cada acción por separado y por eso una misma herramienta va de `Safe` a `Destructive` según lo que le pidan.

Las herramientas de servidores MCP entran por la misma puerta que las propias: se suman al `ToolExecutor`, que es el único lugar donde se decide si algo se ejecuta. Un servidor que no levanta se informa y no impide arrancar.

Lo que estas herramientas pueden hacer efectivamente en la máquina —PowerShell, archivos, control de ventanas, captura de pantalla— está enumerado en `SECURITY.md`. Acá sólo importa la forma: el modelo propone, la política local decide, y la decisión no depende de lo que el modelo diga sobre sí mismo.

## Recordatorios y agenda

`ReminderScheduler` inspecciona periódicamente `assistant-data.json` y levanta dos eventos que el shell traduce en presencia: `ReminderDue` cuando vence un recordatorio y `AgendaItemDue` cuando empieza un evento. No dibuja nada ni sale a la red.

Cada ítem se estampa como avisado **antes** de emitir el evento: una caída entre el sello y el aviso pierde una notificación, que es preferible a repetir la misma alerta en cada arranque. Lo atrasado más allá de la ventana de gracia se estampa en silencio, y un evento cuya hora de fin ya pasó tampoco se anuncia. Hay un techo de avisos por pasada, compartido entre las dos listas.

Hasta esta versión la clase sólo leía recordatorios y `AgendaItem` no tenía dónde anotar que ya se había avisado: la agenda se podía escribir y leer, pero no sonaba nunca.

## Persistencia local

| Archivo | Contenido | Estado de conexión |
|---|---|---|
| `%LOCALAPPDATA%\Viernes\window.json` | posición del orbe | conectado al shell |
| `%LOCALAPPDATA%\Viernes\assistant-data.json` | recordatorios y agenda local, con sus sellos de aviso | conectado a tools/comandos y al vigía |
| `%LOCALAPPDATA%\Viernes\settings.json` | mute, modo wake/PTT, frases y proveedor, sin secretos | conectado al runtime de voz |
| `%LOCALAPPDATA%\Viernes\memory.json` | recuerdos, observaciones y sugerencias | comandos explícitos; observación pausada de fábrica |
| `%LOCALAPPDATA%\Viernes\reglas.json` | reglas que el usuario enseñó | se inyectan en cada turno |
| `%LOCALAPPDATA%\Viernes\objetivos.json` | objetivos abiertos y su avance | conectado a la tool `objetivo` |
| `%LOCALAPPDATA%\Viernes\misiones.json` | encargos que sobreviven a la charla | conectado a la tool `mision` |
| `%LOCALAPPDATA%\Viernes\autonomia.json` | hasta dónde puede llegar sola, por acción y persona | conectado a la tool `permiso` |
| `%LOCALAPPDATA%\Viernes\aprendido.json` | qué acciones funcionaron antes | conectado al recetario de acciones |
| `%LOCALAPPDATA%\Viernes\servidores-mcp.json` | servidores MCP declarados | se leen al iniciar |
| `%LOCALAPPDATA%\Viernes\papelera\` | lo que borró la tool `archivo` | recuperable con `accion=recuperar` |
| `%LOCALAPPDATA%\Viernes\usage-ledger.json` | fecha, rol, modelo, tokens y costo sin contenido | conectado al flujo remoto fast |
| `%LOCALAPPDATA%\Viernes\Models\Whisper\*.bin` | modelo STT local | selector implementado; instalación explícita |

Las escrituras de datos, settings y memoria usan archivo temporal + reemplazo. Ninguno de estos documentos tiene un campo de API key.

## Puntos de extensión

- `ISpeechRecognitionProvider`: proveedor STT local/externo con estado y privacidad visibles.
- `IWakeWordService`: detector local reemplazable; producción requiere evaluación real.
- `IAssistantTool`: nueva capacidad concreta y tipada.
- `IToolPolicy`: reglas de riesgo separadas del modelo.
- `IUserDataStore`: recordatorios y agenda; hoy hay implementación en memoria y en JSON.
- `IRoleAwareChatCompletionClient`: selección explícita de lane, sin ruteo opaco.
- `IPersonalMemoryStore`: memoria revisable, editable y borrable.
- servidores MCP: capacidades de terceros sin recompilar, sujetas a la misma política.

Los conectores futuros de calendario, correo o navegador deben conservar esa separación: contrato, implementación, permiso, confirmación, auditoría y pruebas.
