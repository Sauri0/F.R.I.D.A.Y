# Registro de capacidades

Un modelo no obtiene permisos por ser más capaz. Viernes sólo puede hacer algo cuando existe una implementación concreta, una política de riesgo y una autorización visible.

## Estado actual

| Capacidad | Implementación disponible | Conectada al widget | Permiso / límite actual |
|---|---|---:|---|
| Shell Windows | WPF transparente, topmost, bandeja e instancia única | sí | sin navegador ni servidor local |
| Orbe compacto | `78×78` idle; burbuja temporal `344×112`; entrada `352×158` | sí | no hay panel persistente |
| Arrastre | WPF `DragMove` + `window.json` | sí | restaura sólo posiciones visibles |
| Autorun | `HKCU\...\Run` por usuario | sí | opt-in desde bandeja; sin administrador |
| PTT | proveedor Whisper preferido / SAPI fallback | sí | micrófono sólo durante captura manual |
| Wake word | SAPI local con gramática exacta + handoff | sí; toggle en bandeja | **DEMO/no robusto**, 1–8 frases, micrófono visible |
| STT Whisper | Whisper.net + NAudio, modelo local español | sí | modelo instalado explícitamente; SAPI fallback |
| TTS | SAPI/voz local de Windows | sí | cancelable y sujeto a mute |
| Conversación | OpenRouter chat completions fast | sí, sólo con clave | HTTPS, fallback fast, máximo de iteraciones |
| Selección por rol | fast/agent/reasoning/premium/embeddings/resumen | API de Core | sólo fast en flujo normal; premium opt-in |
| Recordatorios | JSON local + tools create/list + scheduler | sí, con aviso al vencer | orbe al frente, globo de bandeja y voz; sin toast nativo de Windows |
| Agenda local | JSON local + tools create/list | sí, por comandos o tool calls | no sincroniza calendarios externos |
| Búsqueda web | tool placeholder | sí | simulada; no hace red ni abre navegador |
| Acciones Windows | `pc_action` | sí | confirmación; resultado simulado; sensible/destructivo bloqueado |
| Memoria personal | JSON local tipado de tres niveles | sí, por comandos explícitos | observación pausada por defecto; sin extracción automática |
| Tokens/costo | parseo por completion + `UsageLedger` local | sí, en lane fast | sin contenido; preflight por turno |
| Documentos/archivos | no implementado | no | no existe lectura arbitraria de disco |
| Navegación | no implementada | no | no existe control implícito del navegador |
| Calendario externo | no implementado | no | requerirá OAuth y scopes mínimos |

## Herramientas incluidas

| Tool | Riesgo declarado | Efecto |
|---|---|---|
| `reminder_create` | safe | guarda título/fecha en `assistant-data.json`; rechaza texto con forma de credencial |
| `reminder_list` | safe | lista recordatorios locales |
| `agenda_create` | safe | guarda un evento en la agenda interna |
| `agenda_list` | safe | lista la agenda interna |
| `web_search` | safe | devuelve la consulta como simulación; sin red |
| `pc_action` | variable | previewables requieren confirmación; sensibles/destructivas nunca se ejecutan |

`pc_action` sólo reconoce como previsualizables `open_settings`, `open_application` y `show_desktop`; aun con confirmación devuelve una simulación y no invoca el sistema. Acciones como `shutdown`, `run_command`, `change_setting`, borrado, formateo o desinstalación permanecen pendientes/bloqueadas.

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

Reanudar hábitos sólo cambia el permiso/estado del store. El MVP no observa conversaciones ni genera sugerencias automáticamente. Aprobar/rechazar sugerencias y borrar toda la memoria existen en la API, pero aún no tienen comando compacto.

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

No se aceptan shell arbitrario, acceso masivo al disco, elevación, credenciales, compras, envío de mensajes o cambios irreversibles como “tools genéricas”. Para archivos se prevé selector explícito y read-only por defecto; para calendario, OAuth por conector; para navegador, acciones/dominios acotados; para Windows, allowlists pequeñas y operaciones reversibles.

## Criterio de cartera

Agregar más modelos no agrega capacidades. La cartera se mantiene pequeña y configurable; se compara con casos de español rioplatense, exactitud de tool calls, latencia y costo. Una capacidad se considera disponible por su conector probado, no por una afirmación del LLM.
