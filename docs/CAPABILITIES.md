# Registro de capacidades

Un modelo no obtiene permisos por ser más capaz. Viernes sólo puede hacer algo cuando existe una implementación concreta, una política de riesgo y una autorización visible.

Un registro de capacidades desactualizado es peor que ninguno, porque es el lugar donde se supone que mirás para saber qué le estás dando. Ya quedó viejo dos veces: primero describía un MVP que listaba seis herramientas de las dieciséis que había, y después se quedó sin `leer_web` el día que se agregó. Las dos veces lo encontró una auditoría, no una lectura.

## Estado actual

| Capacidad | Implementación disponible | Conectada al widget | Permiso / límite actual |
|---|---|---:|---|
| Shell Windows | WPF transparente, topmost, bandeja e instancia única | sí | sin navegador ni servidor local |
| Orbe compacto | `78×78` idle; burbuja temporal `344×112`; entrada `352×158` | sí | no hay panel persistente |
| Arrastre | WPF `DragMove` + `window.json` | sí | restaura sólo posiciones visibles |
| Autorun | `HKCU\...\Run` por usuario | sí | opt-in desde bandeja; sin administrador |
| Freno de emergencia | `Ctrl+Shift+Alt+J` global | sí | corta turno, voz y comandos; silencia el micrófono |
| PTT | proveedor Whisper preferido / SAPI fallback | sí | micrófono sólo durante captura manual |
| Wake word | SAPI local con gramática exacta + handoff | sí; toggle en bandeja | **DEMO/no robusto**, 1–8 frases, micrófono visible |
| Conversación abierta | bucle de captura continua tras el wake | sí | cierra por frase de despedida, silencio, mute o la tool `descansar` |
| STT Whisper | Whisper.net + NAudio, modelo local español | sí | modelo instalado explícitamente; SAPI fallback |
| TTS | voz neural por OpenRouter con respaldo SAPI local | sí | cancelable, por oraciones y sujeto a mute |
| Conversación | OpenRouter chat completions fast | sí, sólo con clave | HTTPS, fallback fast, máximo de iteraciones |
| Selección por rol | fast/agent/reasoning/premium/embeddings/resumen | API de Core | sólo fast en flujo normal; premium opt-in |
| Recordatorios | JSON local + tools crear/listar/completar/borrar + vigía | sí, con aviso al vencer | orbe al frente, globo de bandeja y voz; sin toast nativo de Windows |
| Agenda local | JSON local + tools crear/listar + **el mismo vigía** | sí, con aviso al empezar | no sincroniza calendarios externos |
| Búsqueda web | resultados del proveedor inyectados en el turno | sí, encendida por defecto | `VIERNES_WEB_SEARCH=false` la apaga; la tool sólo lo declara |
| Acciones Windows | `pc_action` sobre `WindowsPcActionExecutor` | sí | **ejecuta de verdad**; sensible/destructivo sigue bloqueado |
| Pantalla | `pc_action see_screen` + lectura de controles | sí | la captura viaja al modelo y se descarta; no se persiste |
| Archivos | `archivo`: leer, escribir, crear, listar, buscar, mover, copiar, borrar | sí | borrar va a papelera propia y se recupera |
| Shell PowerShell | `comando` | sí | sin elevación, techo de 45 s, sin la clave en el entorno hijo |
| Reglas aprendidas | `aprender` → `reglas.json` | sí | se inyectan en todos los turnos siguientes |
| Objetivos y misiones | `objetivo` y `mision` → `objetivos.json` / `misiones.json` | sí | sobreviven al cierre de la charla y al reinicio |
| Permisos por persona | `permiso` → `autonomia.json` | sí | mandar/publicar/borrar preguntan salvo permiso guardado |
| Proyectos de Claude Code | `proyectos`, sólo lectura de sesiones | sí | no escribe en la sesión ni le contesta |
| Estado del equipo | `estado_equipo` | sí | actividad reciente y carga; sólo mira |
| Memoria personal | JSON local tipado de tres niveles | sí | al cerrar una charla destila hasta dos hechos, que quedan descartados mientras la observación siga pausada —lo está de fábrica— |
| Servidores MCP | cliente oficial, `servidores-mcp.json` | sí | lo que exponga cada servidor, por la misma política local |
| Tokens/costo | parseo por completion + `UsageLedger` local | sí, en lane fast | sin contenido; preflight por turno |
| Calendario y correo externos | no implementados | no | previstos vía MCP; ver `MAIL-Y-CALENDARIO.md` |
| Navegación | leer sí, manejar no | sí | `leer_web` abre una dirección y devuelve su texto; no hay control implícito del navegador |

## Herramientas incluidas

| Tool | Riesgo declarado | Efecto |
|---|---|---|
| `reminder_create` | safe | guarda título/fecha en `assistant-data.json`; rechaza texto con forma de credencial |
| `reminder_list` | safe | lista los recordatorios pendientes con su id; `include_completed` suma los hechos |
| `reminder_update` | safe | marca uno como hecho o lo borra; exige id o un título que corresponda a uno solo |
| `agenda_create` | safe | guarda un evento en la agenda interna |
| `leer_web` | safe | abre una dirección http/https y devuelve su texto; rechaza todo lo que resuelva a una red privada —en cada salto de redirección y en el momento de abrir el socket—, no manda cookies ni credenciales, corta a 4 MB bajados y 12.000 caracteres devueltos, y envuelve lo leído en un marco que le dice al modelo que es contenido ajeno y no una orden |
| `agenda_list` | safe | lista la agenda interna |
| `web_search` | safe | no hace red: declara que los resultados ya vienen inyectados, o que están apagados |
| `pc_action` | variable | ejecuta de verdad las acciones previsualizables; sensibles/destructivas nunca se ejecutan |
| `archivo` | safe | acceso real al disco; borrar es reversible desde la papelera propia |
| `comando` | safe | ejecuta PowerShell sin elevación, con techo de tiempo y salida acotada |
| `estado_equipo` | safe | describe actividad reciente o carga del equipo; sólo lectura |
| `aprender` | safe | guarda o borra una regla que se inyecta en los turnos siguientes |
| `objetivo` | safe | abre, avanza, cierra o lista objetivos duraderos |
| `mision` | safe | encargos que sobreviven al cierre de la charla, con preguntas pendientes |
| `permiso` | safe | guarda hasta dónde puede llegar sola con cada acción y cada persona |
| `proyectos` | safe | lee las sesiones de Claude Code del usuario; no escribe en ellas |
| `descansar` | safe | deja de hablar, cierra la charla o suelta el micrófono, según el nivel |

`pc_action` reconoce como previsualizables `open_settings`, `open_application`, `focus_application`, `close_application`, `minimize_application`, `restore_application`, `show_desktop`, `media_control`, `volume`, `play_music`, `search_web`, `lock_screen`, `see_screen`, `move_cursor`, `click`, `double_click`, `right_click`, `type_text`, `press_key`, `scroll`, `read_controls`, `click_control`, `set_text`, `undo` y `what_did_you_do`. Con un ejecutor conectado —que es la configuración normal del escritorio— **se ejecutan de verdad**, y sin `VIERNES_CONFIRM_ACTIONS=true` se ejecutan sin preguntar. `shutdown`, `restart`, `logoff`, `kill_process`, `run_command`, `change_setting`, borrado, formateo y desinstalación siguen marcadas como sensibles o destructivas y no se ejecutan ni aunque se confirmen.

El riesgo declarado no es el riesgo real de la capacidad: `comando` está marcado `safe` porque su argumento se valida y su salida se acota, no porque ejecutar PowerShell arbitrario sea inofensivo. Lo que de verdad puede hacer en tu equipo está en `SECURITY.md`, y ése es el documento a leer antes de dejarlo suelto.

## Recordatorios y agenda

Las dos listas viven en `assistant-data.json` y las vigila el mismo componente, con la misma regla: se estampa que ya se avisó **antes** de anunciar, así que un reinicio no repite un aviso y, como mucho, se pierde uno.

- un recordatorio suena a su hora de vencimiento; uno completado no suena;
- un evento de agenda suena a su hora de inicio, y si ya terminó se estampa en silencio;
- lo que quedó atrasado más de doce horas se estampa sin anunciar, para que una máquina apagada una semana no descargue todo junto al prender;
- hay un techo de avisos por pasada, compartido entre recordatorios y agenda.

## Comandos locales

Sin modelo remoto se puede invocar la superficie segura de forma determinista:

```text
/ayuda
/recordatorios
/recordar FECHA | TEXTO
/agenda
/evento FECHA | TÍTULO | notas opcionales
/buscar CONSULTA
/pc ACCIÓN [destino]
/memoria
/recordá que DATO
/editar memoria ID | DATO
/olvidar ID
/pausar hábitos
/reanudar hábitos
```

También se reconocen las frases exactas `mis recordatorios`, `mostrame mis recordatorios`, `mi agenda`, `mostrame mi agenda`, `qué recordás de mí` y `mostrame mi memoria`. El modo local no interpreta lenguaje libre como una orden de sistema.

Completar y borrar un recordatorio todavía no tienen comando local: se hacen pidiéndoselo con palabras, y ahí entra `reminder_update`. Sin clave de OpenRouter no hay forma de completarlos desde el widget.

## Cómo entra una capacidad nueva

Toda integración real debe aportar:

1. contrato y esquema de argumentos;
2. implementación concreta identificable;
3. evaluación de riesgo independiente del modelo;
4. permiso o confirmación visible cuando corresponda;
5. resultado auditable y sanitizado;
6. cancelación, timeout y límites de tamaño;
7. pruebas de que el modelo no puede saltarse la política;
8. revocación y scopes mínimos para conectores externos.

Elevación, credenciales, compras y envío de mensajes en nombre del usuario siguen fuera. Para calendario y correo el camino previsto es un servidor MCP con OAuth por conector; para navegador, acciones y dominios acotados.

## Criterio de cartera

Agregar más modelos no agrega capacidades. La cartera se mantiene pequeña y configurable; se compara con casos de español rioplatense, exactitud de tool calls, latencia y costo. Una capacidad se considera disponible por su conector probado, no por una afirmación del LLM.
