# Cerebro, modelos y costo

Viernes no es “un modelo”. Es un orquestador que separa voz, conversación, selección de modelo, herramientas, permisos y memoria. Cambiar el LLM puede mejorar lenguaje o planificación, pero **no concede acceso** a archivos, calendario, navegador ni Windows: cada capacidad requiere un conector concreto y una política local.

## Cartera inicial por rol

| Rol | Valor inicial | Uso previsto | Estado en el MVP |
|---|---|---|---|
| Wake / STT / TTS | local | activación, transcripción y voz | independiente del LLM |
| Fast daily | `openai/gpt-5.6-luna` | charla breve, clasificación y tool calls simples | lane usado por el widget |
| Fast fallback | `~google/gemini-flash-latest` | continuidad y alternativa medida | fallback secuencial fast |
| Agent / planning | `openai/gpt-5.6-terra` | automatizaciones y acciones de varios pasos | configurable; no elegido automáticamente |
| Reasoning / documentos | `~anthropic/claude-sonnet-latest` | planificación difícil y documentos complejos | configurable; no elegido automáticamente |
| Premium | ninguno | excepción de gran complejidad | requiere modelo + aprobación explícita |
| Embeddings | local preferido | recuperación de recuerdos aprobados | contrato; proveedor remoto opcional |
| Resumen | local preferido | destilar memoria sin enviar conversaciones | contrato; proveedor remoto opcional |

Los nombres son candidatos iniciales, no una promesa de calidad ni disponibilidad. Los aliases `~autor/familia-latest` reducen roturas por deprecación, pero pueden resolver a versiones diferentes; un benchmark reproducible debe fijar un slug exacto. OpenRouter documenta la [resolución de aliases `latest`](https://openrouter.ai/docs/guides/routing/routers/latest-resolution), los [fallbacks](https://openrouter.ai/docs/guides/routing/model-fallbacks) y [tool calling](https://openrouter.ai/docs/guides/features/tool-calling).

Al configurar o cambiar un modelo hay que comprobar que el endpoint seleccionado soporte `tools`, que respete el esquema de argumentos y que la cuenta tenga acceso. El cliente actual envía `tools` y `tool_choice: auto`; no debe suponerse que cualquier modelo será compatible.

## Configuración sin recompilar

| Variable | Función | Default |
|---|---|---|
| `VIERNES_OPENROUTER_FAST_MODEL` | fast principal | `openai/gpt-5.6-luna` |
| `VIERNES_OPENROUTER_FAST_FALLBACK_MODELS` | lista fast separada por comas | `~google/gemini-flash-latest` |
| `VIERNES_OPENROUTER_AGENT_MODEL` | agent/planning | `openai/gpt-5.6-terra` |
| `VIERNES_OPENROUTER_PLANNING_MODEL` | alias de compatibilidad para agent | Terra si no se configura |
| `VIERNES_OPENROUTER_REASONING_MODEL` | reasoning | `~anthropic/claude-sonnet-latest` |
| `VIERNES_OPENROUTER_PREMIUM_MODEL` | premium | sin valor |
| `VIERNES_OPENROUTER_EMBEDDINGS_MODEL` | embeddings remoto opcional | sin valor |
| `VIERNES_OPENROUTER_LOCAL_SUMMARY_MODEL` | resumen remoto opcional | sin valor |
| `VIERNES_PREFER_LOCAL_EMBEDDINGS` | impide remoto salvo opt-in | `true` |
| `VIERNES_PREFER_LOCAL_SUMMARY` | impide remoto salvo opt-in | `true` |

`OPENROUTER_API_KEY` es la única credencial y se maneja por separado. Ninguna variable de modelo es secreta.

Por compatibilidad se aceptan `VIERNES_OPENROUTER_MODEL` y `VIERNES_OPENROUTER_FALLBACK_MODELS`; las variables `FAST_*` tienen precedencia.

## Selección óptima por acción

La política objetivo es simple y medible:

1. fast resuelve conversación cotidiana, clasificación y una herramienta simple;
2. agent sólo entra para varios pasos o automatización real;
3. reasoning se reserva para análisis/documentos que justifiquen latencia y costo;
4. premium nunca se selecciona sin una decisión visible del usuario;
5. embeddings y resumen permanecen locales siempre que sea viable.

El **comportamiento actual** es más conservador: `ConversationOrchestrator` usa fast. `ModelPortfolio` puede seleccionar otros roles sólo cuando el llamador los pide de forma explícita; no hay escalamiento automático ni clasificador que incurra en gasto adicional. Los fallbacks sólo se agregan al lane fast.

La selección final debe basarse en un set pequeño de casos reales, no en la cantidad de modelos:

- español rioplatense y correcciones de transcripción;
- tool calls correctos, argumentos válidos y respeto de confirmaciones;
- latencia a primera respuesta y duración total;
- costo por tarea completada;
- calidad en agenda, recordatorios, documentos y flujos de varios pasos;
- tasa de corrección humana, falsos éxitos y acciones bloqueadas.

## Fallback

El cliente intenta fast principal y luego la lista configurada, sin duplicados. Pasa al siguiente candidato ante error de transporte, `404`, `408`, `409`, `429` o `5xx`. Errores de credencial y solicitudes inválidas no se disfrazan con fallback. El modelo realmente devuelto por el proveedor se conserva en el resultado.

## Tokens, costo y presupuestos

Cada completion puede aportar:

- tokens de entrada y salida;
- modelo resuelto;
- costo exacto desde `usage.cost`, si el proveedor lo informa;
- costo estimado desde una rate card configurada, si no hay exacto.

La estimación nunca usa una tarifa hardcodeada. `VIERNES_OPENROUTER_RATES_JSON` espera un objeto por slug exacto:

```powershell
$env:VIERNES_OPENROUTER_RATES_JSON = '{"openai/gpt-5.6-luna":{"inputUsdPerMillion":0.1,"outputUsdPerMillion":0.2}}'
```

Los números del ejemplo son ilustrativos: deben reemplazarse por tarifas verificadas al momento de configurar. Si no hay costo exacto ni rate card, el resultado queda como costo desconocido. Esa completion sigue contando requests y tokens, pero un presupuesto monetario no puede contabilizar un costo que no conoce.

Contratos disponibles:

| Variable | Alcance |
|---|---|
| `VIERNES_OPENROUTER_DAILY_BUDGET_USD` | presupuesto diario global |
| `VIERNES_OPENROUTER_MONTHLY_BUDGET_USD` | presupuesto mensual global |
| `VIERNES_OPENROUTER_MAX_REQUESTS_PER_DAY` | solicitudes diarias globales |
| `VIERNES_MAX_DEEP_TASKS_PER_DAY` | cuota agent/reasoning prevista; default `3` |
| `VIERNES_OPENROUTER_<ROL>_DAILY_BUDGET_USD` | diario por rol |
| `VIERNES_OPENROUTER_<ROL>_MONTHLY_BUDGET_USD` | mensual por rol |
| `VIERNES_OPENROUTER_<ROL>_MAX_REQUESTS_PER_DAY` | solicitudes por rol |

`<ROL>` admite `FAST`, `AGENT`, `REASONING`, `PREMIUM`, `EMBEDDINGS` y `LOCAL_SUMMARY`.

### Ledger y límite actual

`UsageLedger` persiste por defecto en `%LOCALAPPDATA%\Viernes\usage-ledger.json`. Guarda sólo id de request, fecha UTC, rol, modelo, tokens, costo exacto/estimado y flag de tarea profunda; retiene hasta 10.000 entradas/400 días, calcula totales diarios/mensuales/globales/por rol y devuelve `RequiresExplicitApproval` si la próxima petición alcanzaría un límite. Un override debe declararse explícitamente.

El ledger no ve ni guarda prompts, respuestas, audio, claves, conversation ids ni argumentos de tools. El runtime WPF lo consulta antes de cada turno remoto fast. El cliente está envuelto por un decorator que registra modelo/tokens/costo de cada completion exitosa, incluidas las iteraciones posteriores a tools. Si el guard devuelve `RequiresExplicitApproval`, el widget no llama al modelo; todavía no expone un override de presupuesto en la UI.

El chequeo fast actual no aporta `EstimatedRequestCostUsd`, así que un límite monetario sólo puede decidir con el costo ya registrado y el nuevo costo se anota después de cada completion. Los límites de conteo sí se conocen antes, pero el preflight se hace una vez por turno; un loop de tools puede sumar varias completions antes del próximo chequeo. `EvaluateAsync` es un snapshot y no reserva presupuesto frente a requests concurrentes. La cuota profunda se aplica cuando el llamador marca `IsDeepTask`; como agent/reasoning aún no participan del flujo normal, esos lanes deberán conectarse explícitamente.

## Qué cambia al agregar la clave

Sin `OPENROUTER_API_KEY`, el cliente retorna modo local y no hace red. Con clave:

1. el turno y las definiciones de tools se envían por HTTPS a OpenRouter;
2. el audio nunca se envía a OpenRouter; STT produce texto antes;
3. el modelo puede proponer tool calls, pero no ejecutarlas por sí mismo;
4. la política local valida tool, argumentos y riesgo;
5. el resultado de la herramienta vuelve al modelo para redactar la respuesta;
6. modelo/tokens/costo se agregan al ledger local sin contenido.

## Memoria no es entrenamiento

Viernes no reentrena el modelo base ni promete aprendizaje continuo oculto. `Viernes.Memory` implementa:

1. **recuerdo explícito:** el usuario pidió guardarlo;
2. **observación temporal:** hecho destilado, confianza, fecha y vencimiento; captura pausada por defecto;
3. **sugerencia:** vence y nunca se vuelve explícita sin aprobación.

La API permite revisar, listar por tipo, editar, olvidar, borrar todo, aprobar/rechazar sugerencias y pausar/reanudar observaciones. Rechaza credenciales, transcripciones y contenido con forma de conversación. El runtime conecta comandos para revisar, guardar explícitamente, editar, olvidar y cambiar la pausa. Todavía no destila conversaciones en observaciones ni crea/acepta sugerencias automáticamente; por eso el widget no “aprende” nada en silencio.
