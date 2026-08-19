# Voz local

La voz se divide en wake word, reconocimiento (STT) y síntesis (TTS). El LLM no recibe audio.

```text
wake local ──activa──> captura acotada ──> Whisper/SAPI ──texto──> Core/OpenRouter
                                                                └──> tools locales
respuesta de texto ──> TTS SAPI local
```

## Estado del ejecutable y de la plataforma

- El runtime WPF selecciona Whisper local y vuelve a SAPI si no está disponible.
- PTT, captura de una frase, wake SAPI DEMO y TTS local están conectados.
- Wake se intenta iniciar por defecto, conserva estado en settings y tiene toggle en la bandeja.
- El runtime pausa wake durante PTT, procesamiento y TTS; lo reanuda al finalizar si seguía habilitado.

El recorrido hands-free es testeable, pero no debe presentarse como robusto. PTT y texto son los fallbacks estables.

## Instalar Whisper local

Desde la raíz:

```powershell
.\scripts\setup-whisper.ps1
```

Opcionalmente:

```powershell
.\scripts\setup-whisper.ps1 -Model small
```

El script usa los modelos publicados para [whisper.cpp](https://github.com/ggml-org/whisper.cpp); el runtime .NET es [Whisper.net](https://www.nuget.org/packages/Whisper.net/). El script:

1. descarga `ggml-base.bin` o `ggml-small.bin` desde el repositorio de modelos de `whisper.cpp`;
2. verifica el SHA-1 publicado;
3. guarda el archivo bajo `%LOCALAPPDATA%\Viernes\Models\Whisper`;
4. no sobreescribe un archivo cuyo hash no coincide;
5. no lee ni crea credenciales.

La ruta esperada por defecto es `%LOCALAPPDATA%\Viernes\Models\Whisper\ggml-base.bin`. Los modelos `.en` no sirven para español; el proveedor configura lenguaje `es`. El store de settings y `VIERNES_WHISPER_MODEL_PATH` admiten una ruta `.bin` dentro del directorio de modelos de Viernes, aunque el widget no expone un selector visual.

## Proveedores STT

`SpeechRecognitionProviderSelector` aplica este orden:

1. Whisper local, si el modelo existe y pasa validaciones;
2. SAPI, con el motivo de fallback disponible para la UI;
3. error explícito y entrada de texto si ninguno puede reconocer.

Whisper usa Whisper.net/Runtime y NAudio. La configuración limita duración mínima/máxima, timeout de cierre y dispositivo. El runtime CPU estable puede exigir Windows 11/Server 2022, Visual C++ Redistributable 2022 y CPU con AVX/AVX2/FMA/F16C; en un equipo incompatible debe utilizarse SAPI, no ocultar el fallo.

SAPI busca una cultura `es-AR` o, si no existe, un reconocedor del mismo idioma. Su calidad depende del paquete de voz de Windows y no es equivalente a Whisper.

## Validación técnica actual

Se ejecutó un smoke local sin OpenRouter ni STT externo:

- SAPI sintetizó `Hola Viernes, recordame tomar mate a las cinco de la tarde.` a WAV temporal, español, 16 kHz mono;
- Whisper.net CPU `1.9.1` procesó el audio con `ggml-base.bin` multilingüe;
- resultado: `Hola viernes, recuerda me tomá armate al a 5 de la tarde.`;
- éxito técnico: `true`; confianza agregada: `0.695`; latencia observada en esa máquina: `2164 ms`;
- el WAV y los temporales se eliminaron al terminar.

Esto prueba captura/decodificación/transcripción local de extremo a extremo, pero **no prueba precisión suficiente**. Los errores son evidencia para evaluar el modelo `small`, parámetros, micrófonos y voces reales en español rioplatense. La latencia tampoco es un benchmark transferible a otro hardware.

## Wake word

Defaults:

```text
Viernes
Hola Viernes
```

`WakeWordServiceOptions` acepta de una a ocho frases normalizadas, de 2 a 40 caracteres, cultura `es-AR` y confianza mínima `0.78`. `ViernesLocalSettings` puede persistir esas frases sin secretos.

Opciones de entorno para pruebas:

```text
VIERNES_WAKE_ENABLED=true|false
VIERNES_WAKE_PHRASES=Viernes;Hola Viernes
VIERNES_STT_PROVIDER=sapi
VIERNES_WHISPER_MODEL_PATH=%LOCALAPPDATA%\Viernes\Models\Whisper\ggml-base.bin
```

Omitir `VIERNES_STT_PROVIDER` conserva Whisper-first. La ruta Whisper se restringe por defecto al directorio local de Viernes.

El detector incluido usa SAPI con una gramática exacta y expone `IsDemoOnly = true` junto con un aviso de fiabilidad. Es una **demostración**, no un wake engine robusto. Puede fallar por ruido, distancia, acento, paquete de voz o micrófono, y también producir activaciones falsas.

Un reemplazo de producción debe medirse con:

- falsos positivos por hora;
- falsos negativos por hablante/frase;
- español rioplatense y distintas voces;
- ruido doméstico, TV, música y conversaciones;
- distancia, tipo de micrófono y CPU;
- tiempo de activación y consumo sostenido.

PTT siempre permanece como alternativa.

## Handoff wake → frase

`WakeWordRecognitionCoordinator` sigue una regla estricta:

1. comprueba que wake estaba escuchando;
2. lo detiene y verifica que el micrófono se liberó;
3. pide al proveedor STT una sola frase;
4. cancela cualquier captura residual;
5. reanuda wake sólo si antes correspondía y no está muted.

Defaults de captura hands-free:

- silencio inicial: `8 s`;
- silencio final: `850 ms`;
- duración máxima: `15 s`;
- umbral energético Whisper: `0.018`.

Ese umbral es un VAD simple de demostración y puede necesitar calibración; nunca se presenta como prueba de voz robusta.

## Indicadores y mute

- Micrófono inactivo: badge oscuro junto al orbe.
- Micrófono activo: badge visible/verde y estado `Escuchando` durante captura.
- Mute: detiene wake/captura, libera el dispositivo y cancela TTS.
- PTT: mantener el núcleo; soltar finaliza y transcribe.
- Toque corto: cancela la captura iniciada y abre texto.

Con wake activo, el indicador permanece encendido aun si el orbe está idle. La bandeja muestra el estado y permite apagar wake sin cerrar la app.

Ocultar el orbe cancela la frase en curso pero mantiene la escucha: con `ListenWhileHidden` —activado por defecto, con toggle en la bandeja y override por `VIERNES_LISTEN_WHILE_HIDDEN`— Viernes sigue atento y **se muestra solo al oír su nombre**, sin robar el foco del teclado. Mute es el corte duro que libera el micrófono. Salir dispone todos los servicios.

## Privacidad operacional

- Audio y parciales se procesan localmente.
- No se guardan audio ni transcripciones en memoria o telemetría.
- OpenRouter recibe el texto final, no el stream del micrófono.
- El modelo Whisper sólo se instala por acción explícita.
- Un STT externo futuro requerirá consentimiento y credencial independiente.

## TTS

La respuesta se sintetiza con una voz local de Windows compatible con `es-AR` cuando está disponible. La salida se limita a 1.200 caracteres por intervención, se puede cancelar y respeta mute. STT/TTS no consumen tokens de OpenRouter.

## Sesión en vivo (Gemini Live)

Es el segundo camino de voz y **viene apagado**. El de siempre —Whisper local, modelo por OpenRouter, síntesis local— sigue intacto y es el que corre si éste no está encendido o no puede abrirse.

Lo que cambia no es sólo que tarda menos: el micrófono queda abierto mientras Viernes habla, así que **hablarle encima la calla**. El camino de siempre no puede hacer eso por más que se lo apure, porque ahí, mientras habla, no hay nadie escuchando.

### Encenderlo

Hacen falta las dos cosas, y las dos se leen primero de `%LOCALAPPDATA%\Viernes\claves.json` y después del entorno:

```
GOOGLE_API_KEY   la clave de Google
VIERNES_LIVE     1 · true · si     enciende el camino nuevo
```

Opcionales, con sus valores por defecto entre paréntesis:

```
VIERNES_LIVE_MODEL        (gemini-3.1-flash-live-preview)
VIERNES_LIVE_VOICE        (Aoede)
VIERNES_LIVE_SILENCE_MS   (700)  silencio que el servidor espera para cerrar tu turno
VIERNES_LIVE_CHUNK_MS     (20)   cuánto audio entra en cada fragmento que se sube
```

Un valor mal escrito no impide arrancar: se cae al de por defecto.

### Cómo se elige el camino

Se elige al abrir una conversación —el nombre o el toque en el orbe—, una sola vez, y el motivo queda escrito en `%LOCALAPPDATA%\Viernes\trace.log`:

```
voz.camino.inicial   vivo · hay clave de Google y la sesión en vivo está encendida
voz.camino           siempre · falta la clave de Google
vivo.abierta
vivo.momento         Listening → Speaking
vivo.caida           Se cortó la sesión en vivo y no pude reconectar.
vivo.cerrada         Conversación cerrada
```

La línea nunca lleva la clave adentro: quien decide recibe *si hay* clave, no la clave.

### Estados del orbe

La sesión sabe cuatro momentos y sólo cuatro; el resto de lo que dibuja el orbe —guardia, sin clave, sorda, un proyecto esperando— es una condición del asistente y la sigue decidiendo quien la decidía.

| Momento | Estado | De dónde sale |
|---|---|---|
| te escucho | `Listening` | turno en reposo |
| pensando | `Thinking` | dejaste de hablar y todavía no volvió nada |
| hablando | `Speaking` | está llegando o sonando audio |
| interrumpida | `Interrupted` | `serverContent.interrupted` |

**«Pensando» no lo manda el servidor.** Entre que el servidor da por cerrada tu frase y el primer bloque de voz de la respuesta no hay ningún mensaje en el protocolo. Ese momento lo pone el detector de voz local —el mismo modelo entrenado que usa el oído continuo— cuando ve que dejaste de hablar y el turno sigue en reposo.

### Cuándo se cae al camino de siempre

Sin que el usuario tenga que hacer nada, y siempre con el motivo en la traza: si falta la clave, si está apagado, si el servidor no acepta la sesión, si no se puede abrir el micrófono, o si la sesión se muere en el medio de la charla —ahí la conversación no se corta, se muda—.

Después de una caída el camino nuevo queda trabado un rato y la espera crece con cada caída seguida, hasta media hora. Sin eso, «se cae al camino de siempre» duraría una conversación: la siguiente volvería a intentar y a esperar la conexión, y desde afuera eso no se ve como un servicio caído sino como un asistente que tarda de más cada vez que le hablás.

### Lo que el camino nuevo todavía no tiene

**Herramientas.** El `setup` que se manda no declara ninguna, así que en vivo se conversa pero no se abre una aplicación ni se crea una carpeta ni se anota un recordatorio. Está dicho en la instrucción de sistema para que no prometa lo que no puede, y escribir cierra la sesión hablada y devuelve el turno al camino de siempre, que sí las tiene.

### Cierres automáticos

Un minuto sin que nadie hable cierra la charla, igual que en el camino de siempre. No es prolijidad: la sesión manda audio del micrófono a la nube sin parar y se cobra por minuto de audio —unos USD 0,005 el minuto que sube y 0,018 el que baja—, así que un cuarto vacío con el orbe abierto es plata.
