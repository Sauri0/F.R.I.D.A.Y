# Arquitectura de Viernes

Viernes separa presentación, voz/plataforma, conversación, capacidades y memoria. La interfaz WPF no conoce el protocolo JSON de OpenRouter; un modelo no llama a Windows directamente; y ningún store local acepta credenciales.

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
  │    └─ conversación + confirmaciones
  ├─ Viernes.Platform.Windows
  │    ├─ Whisper local preferido / SAPI fallback
  │    ├─ wake-word SAPI DEMO + handoff de micrófono
  │    ├─ TTS SAPI local
  │    ├─ autorun HKCU por usuario
  │    └─ preferencias locales sin secretos
  ├─ Viernes.Core
  │    ├─ cliente OpenRouter, tools y fallback fast
  │    ├─ cartera de modelos seleccionada por rol
  │    ├─ tokens/costo por completion, ledger y presupuestos
  │    ├─ herramientas concretas
  │    └─ política local de riesgo
  └─ Viernes.Memory
       └─ store local de memoria consentida + comandos explícitos
```

## Shell compacto

La ventana es nativa, transparente, sin marco, topmost y fuera de la barra de tareas. En reposo sólo renderiza el orbe; no mantiene un panel. Una interacción abre una burbuja con estado/transcripción/respuesta breve y vuelve al modo mínimo siete segundos después de presentar un resultado. Las confirmaciones y la entrada escrita son expansiones explícitas, no superficies permanentes.

El área exterior del orbe ejecuta `DragMove`. Al ocultarse o cerrarse, la posición se guarda en `%LOCALAPPDATA%\Viernes\window.json`. En el próximo inicio se valida contra todas las áreas de trabajo y se usa una posición segura si el punto quedó fuera de pantalla.

`NotifyIcon` mantiene mostrar/ocultar, mute, wake demo, autorun y salida. El proceso es de instancia única. El cierre normal de la ventana la oculta y pausa wake por privacidad; al mostrarla se reanuda si seguía habilitado. `Salir de Viernes` dispone voz, bandeja y runtime.

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

### Wake word, contrato implementado

```text
SAPI wake DEMO ──detección──> detener wake y liberar micrófono
                                      │
                                      v
                         captura acotada de una frase
                                      │
                              Whisper / SAPI STT
                                      │
                                      v
                               texto → Core/tools
                                      │
                           reanudar wake si correspondía
```

`WakeWordRecognitionCoordinator` garantiza que wake y STT no usen el dispositivo al mismo tiempo. La frase posterior tiene timeout de silencio inicial, silencio final y duración máxima. En Whisper el corte usa un VAD energético simple: ayuda a acotar la captura, pero también es una demo sensible al ruido.

`AssistantRuntime` carga preferencias, selecciona Whisper/SAPI, inicia wake por defecto cuando está disponible, pausa wake durante PTT/requests/TTS y lo reanuda al finalizar. La bandeja permite apagarlo. El recorrido está conectado, pero wake SAPI y el VAD todavía son demos: PTT y texto siguen siendo fallbacks confiables.

## Cerebro por roles

`ModelPortfolio` contiene modelos por rol y sólo devuelve el rol solicitado por el llamador:

- fast: conversación diaria y tools simples;
- agent/planning: varios pasos;
- reasoning: análisis difícil/documentos;
- premium: requiere modelo configurado y `PremiumApproved`;
- embeddings/resumen: prefiere ejecución local y requiere opt-in para remoto.

El cliente normal de `ConversationOrchestrator` selecciona **fast**. Los otros perfiles son contratos utilizables por flujos explícitos; no existe un router oculto que suba de costo por sí solo. Los fallbacks configurados se aplican actualmente al lane fast.

Cada respuesta remota retorna modelo, tokens de entrada/salida y costo exacto del proveedor o una estimación desde una rate card configurada. `AssistantRuntime` consulta `UsageLedger` antes de un turno fast y evita iniciarlo si el guard no permite continuar. Un decorator registra cada completion remota exitosa, también las iteraciones posteriores a tools. El ledger calcula totales diarios/mensuales/globales/por rol sin contenido. El preflight es un snapshot por turno, no reserva presupuesto y no aporta una estimación previa; un turno de varias completions puede cruzar el límite y el siguiente quedará bloqueado. Los lanes profundos aún no están conectados al widget.

## Herramientas y permisos

`IAssistantTool` aporta nombre, descripción, esquema JSON, riesgo y ejecutor. `SafeToolPolicy` toma la decisión independientemente del LLM:

- `Safe`: se puede ejecutar tras validar argumentos;
- `RequiresConfirmation`: se ejecuta sólo después de una confirmación vigente;
- `Sensitive` y `Destructive`: permanecen pendientes/bloqueadas en este MVP, incluso si se confirma;
- tool desconocida o argumentos inválidos: rechazo.

La confirmación expira a los 15 minutos y el núcleo limita el número de iteraciones de tools. `pc_action` nunca llama a `Process.Start`, shell, filesystem ni Win32: incluso las acciones permitidas son simulaciones.

## Persistencia local

| Archivo | Contenido | Estado de conexión |
|---|---|---|
| `%LOCALAPPDATA%\Viernes\window.json` | posición del orbe | conectado al shell |
| `%LOCALAPPDATA%\Viernes\assistant-data.json` | recordatorios y agenda local | conectado a tools/comandos |
| `%LOCALAPPDATA%\Viernes\settings.json` | mute, modo wake/PTT, frases y proveedor, sin secretos | conectado al runtime de voz |
| `%LOCALAPPDATA%\Viernes\memory.json` | recuerdos, observaciones y sugerencias | comandos explícitos conectados; observación automática ausente |
| `%LOCALAPPDATA%\Viernes\usage-ledger.json` | fecha, rol, modelo, tokens y costo sin contenido | conectado al flujo remoto fast |
| `%LOCALAPPDATA%\Viernes\Models\Whisper\*.bin` | modelo STT local | selector implementado; instalación explícita |

Las escrituras de datos, settings y memoria usan archivo temporal + reemplazo. Ninguno de estos documentos tiene un campo de API key.

## Puntos de extensión

- `ISpeechRecognitionProvider`: proveedor STT local/externo con estado y privacidad visibles.
- `IWakeWordService`: detector local reemplazable; producción requiere evaluación real.
- `IAssistantTool`: nueva capacidad concreta y tipada.
- `IToolPolicy`: reglas de riesgo separadas del modelo.
- `IRoleAwareChatCompletionClient`: selección explícita de lane, sin ruteo opaco.
- `IPersonalMemoryStore`: memoria revisable, editable y borrable.

Los conectores futuros de calendario, navegador, archivos o Windows deben conservar esa separación: contrato, implementación, permiso, confirmación, auditoría y pruebas.
