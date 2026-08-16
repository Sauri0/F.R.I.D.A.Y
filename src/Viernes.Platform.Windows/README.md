# Viernes.Platform.Windows

Servicios Windows consumibles desde la aplicación WPF, sin dependencias de UI.

## APIs principales

- `AutoStartService`: consulta, activa y desactiva `HKCU\...\Run` únicamente para el usuario actual. El comando registrado siempre es la ruta absoluta al `.exe` entre comillas.
- `SpeechService`: reconocimiento local por push-to-talk y síntesis con Windows SAPI. `StartPushToTalkAsync` abre el micrófono; `StopPushToTalkAsync`, `CancelPushToTalkAsync`, mute y `DisposeAsync` lo liberan.
- `SpeechRecognitionProviderSelector`: prefiere Whisper local cuando el modelo configurado
  existe y usa SAPI como fallback explicando el motivo. No descarga modelos.
- `SapiWakeWordService`: demo experimental/no confiable de palabra de activación con
  gramática exacta (`Viernes`, `Hola Viernes` o frases personalizadas). Su indicador de
  micrófono permanece activo durante la escucha y mute libera el dispositivo.
- `WakeWordRecognitionCoordinator`: detiene wake-word antes de capturar una sola frase,
  usa límites de silencio/duración y luego reanuda wake-word si correspondía.
- `LocalSettingsStore`: preferencias no sensibles en `%LOCALAPPDATA%\Viernes\settings.json`, escritas mediante reemplazo atómico.

La UI puede enlazar el indicador de privacidad a `IsMicrophoneActive` y a
`MicrophoneActivityChanged`. Las transcripciones parciales/finales llegan por
`TranscriptionUpdated`. Para un botón PTT, usar `StartPushToTalkAsync` en `MouseDown`
y `StopPushToTalkAsync` en `MouseUp`; ante pérdida de captura, usar
`CancelPushToTalkAsync`.

Los eventos de SAPI pueden llegar desde un hilo de trabajo. La capa WPF debe trasladar
las mutaciones visuales a su `Dispatcher`.

## Privacidad y límites

- El reconocimiento SAPI ocurre localmente y solo durante una sesión PTT explícita.
- Whisper también es local: el modelo predeterminado se espera en
  `%LOCALAPPDATA%\Viernes\Models\Whisper\ggml-base.bin`. La aplicación no lo descarga.
- El VAD energético y wake-word SAPI son demostraciones sensibles al ruido; no se afirma
  que sean detectores robustos. Wake-word solo se activa mediante `StartAsync` explícito.
- No hay hooks globales ni control del equipo.
- `ViernesLocalSettings` no posee campos de claves o tokens. La futura clave de
  OpenRouter debe provenir de una variable de entorno y nunca pasar por este store.
- La disponibilidad de reconocimiento/voz española depende de los paquetes de idioma
  instalados en Windows; consultar `GetCapabilities()` para adaptar la UI.

## Dependencia

- `System.Speech` 10.0.0
- `Whisper.net` y `Whisper.net.Runtime` 1.9.1 (CPU local)
- `NAudio.WinMM` 2.3.0

El runtime CPU estable de Whisper.net requiere Windows 11/Server 2022, Visual C++
Redistributable 2022 y CPU AVX/AVX2/FMA/F16C. Si falta cualquier requisito o el modelo,
la selección vuelve a SAPI. El proyecto apunta a `net10.0-windows`.
