# F.R.I.D.A.Y

Un asistente de escritorio para Windows que vive en una gota de vidrio, escucha cuando lo llamás y
hace cosas en tu máquina: abre aplicaciones, maneja archivos, pone música, mira la pantalla, busca en
la web y aprende cómo trabajás.

Habla en rioplatense. Vos elegís cómo se llama.

---

## Instalación

1. Bajá la carpeta [`instalador/`](instalador/) — necesitás `INSTALAR.cmd` e `instalar.ps1` juntos.
2. Doble clic en **`INSTALAR.cmd`**.
3. Contestá tres cosas: cómo se va a llamar, tu clave de OpenRouter y —si querés— la de Google, que
   es opcional y se puede dejar para después.

El instalador se encarga del resto: baja la aplicación, baja el modelo de voz y el detector, crea los
accesos directos y lo deja andando. **No hace falta instalar .NET** — viene adentro del paquete.

Todo queda en tu carpeta de usuario, así que no hace falta ser administrador.

### Qué necesitás

| | |
|---|---|
| Sistema | Windows 10 o posterior, 64 bits |
| Espacio | ~700 MB (200 la aplicación, 465 el modelo de voz, 16 el detector) |
| Micrófono | Cualquiera |
| Clave | Una de [OpenRouter](https://openrouter.ai/keys), gratis de sacar |

---

## Actualizar sin perder nada

**Corré el mismo `INSTALAR.cmd` de siempre.** Ése es todo el procedimiento, y se puede repetir
cuantas veces haga falta.

Lo que se reemplaza es **la aplicación entera**: lo visual, las funciones, el conector, todo lo que
haya cambiado entre una versión y la otra.

Lo tuyo no se toca. La aplicación vive en `%LOCALAPPDATA%\Viernes\app`, y una actualización pisa
**sólo esa carpeta**. Un piso más arriba, intactos, quedan:

- el nombre que le pusiste y tus preferencias — `settings.json`;
- la clave de Google, si la pusiste — `claves.json`;
- la memoria, lo que aprendió y los permisos que le diste — `memory.json`, `reglas.json`,
  `aprendido.json`, `autonomia.json`;
- las misiones, los objetivos, los recordatorios y la agenda;
- la papelera de la que se recuperan los archivos borrados;
- dónde dejaste el orbe y en qué pantalla — `window.json`, `monitores.json`;
- los 465 MB del modelo de voz y el detector, que no se vuelven a bajar.

La clave de OpenRouter ni siquiera está ahí: vive en las variables de entorno de tu cuenta de
Windows, y el instalador no la pisa sin preguntar. Con la de Google hace lo mismo: si ya tenés una,
te avisa que la tenés y sólo la reemplaza si le decís que sí.

Dos detalles que se notan cuando pasa algo:

- Si ya tenés la última versión, te lo dice y no baja nada.
- Si la descarga se corta a la mitad, **la instalación anterior queda entera**. Lo nuevo se extrae al
  lado y recién cambia de lugar cuando está completo y tiene adentro lo que tiene que tener.

El nombre te lo vuelve a preguntar. Poné el mismo y sigue todo igual; poné otro y se renombra, que es
justamente la forma de cambiárselo.

Los detalles finos —qué archivo es cada cosa, cómo saber qué versión tenés, qué hacer si algo
falla— están en [docs/INSTALACION.md](docs/INSTALACION.md).

---

## El nombre lo elegís vos

El instalador pregunta cómo se va a llamar. Ese nombre entra en la primera línea de su prompt, en el
título de la ventana, en la bandeja del sistema, en el acceso directo, y define cómo lo despertás.

**Lo despertás con dos palabras.** Si lo llamás Ana:

> *«Hola Ana»* · *«Che Ana»* · *«Ey Ana»*

Nunca el nombre solo, y no es capricho: está medido. Un nombre que también es una palabra de todos
los días —el original era uno— dicho al pasar en *«el viernes tengo turno»* entraba con más confianza
que casi todas las llamadas de verdad, así que ningún umbral separaba una cosa de la otra. *«Hola
Ana»* no aparece sola en una conversación ajena; el nombre suelto, sí.

**Lo que no hace falta es la pausa.** El micrófono lo abre una sola cosa y el audio se reparte a
la vez a una ventana rodante de diez segundos, al reconocedor del nombre y al detector de voz, así
que podés nombrarla al final y lo dicho antes se manda con el pedido:

> *«Creame una carpeta en el escritorio, che Ana»*

El recorte no son siempre los diez segundos: llega hasta donde arrancó esa tanda de habla, así que
con la tele puesta no le mete diez segundos de tele adelante del pedido. En la burbuja vas a ver lo
que dijiste **antes** de nombrarla dibujado más apagado, con el encabezado *«recuperado del búfer»*.
Es a propósito: eso no se lo dijiste a ella.

**Para cambiarle el nombre: clic derecho sobre el orbe → «Cómo me llamo…».** Mientras escribís te
muestra cómo va a quedar y con qué frases lo vas a despertar. Al aceptar cambia todo de una vez y sin
reiniciar, incluidas esas frases: si pasa de Ana a Nina, deja de contestar a *«Hola Ana»* y contesta
a *«Hola Nina»*. Si algo no se puede cambiar en caliente —el oído no volvió a arrancar, hay una charla
hablada abierta— te lo dice en vez de dejarte creer que sí.

También se puede desde la bandeja, que es la única puerta cuando el orbe está guardado, o desde el
instalador con `INSTALAR.cmd -Reconfigurar`.

No perdés nada al renombrar: la carpeta de datos se llama siempre igual porque identifica al
producto, no al asistente.

Si preferís despertarlo con otra cosa, la variable `VIERNES_WAKE_PHRASES` reemplaza las tres frases
por las tuyas —hasta ocho, separadas por punto y coma—. Ahí sí podés poner el nombre suelto, sabiendo
lo que eso trae.

---

## Tu clave es tuya

**Este repositorio no contiene ninguna clave, y nunca va a contener una.**

Son dos credenciales, y viven en lugares distintos a propósito.

**`OPENROUTER_API_KEY`** — obligatoria. La ponés en la instalación y se guarda como variable de
entorno de tu cuenta de Windows. Por ahí pasan todos los modelos de texto.

**`GOOGLE_API_KEY`** — opcional. Sólo sirve para la conversación hablada en tiempo real. Ésta sí va
en un archivo, `%LOCALAPPDATA%\Viernes\claves.json`, porque abrir, pegar y guardar es más simple que
aprender `setx`. El archivo vive fuera del repositorio y el nombre está en el `.gitignore` igual, por
si algún día alguien lo copia acá para depurar algo.

**Sin la de Google el asistente anda completo**, por el camino de siempre: la escuchás cuando terminó
de pensar en vez de mientras piensa.

Las dos, sin excepción:

- No se escriben en ningún archivo del proyecto.
- No se mandan a ningún lado que no sea su propia API.
- No aparecen en la bitácora, ni en un mensaje de error, ni en una excepción — los `catch` de la
  sesión en vivo copian el *tipo* de la excepción y nunca el mensaje, y eso está comentado donde
  corresponde para que nadie lo "simplifique".
- No viajan a los procesos que el asistente ejecuta por vos: cuando corre un comando de PowerShell,
  las borra explícitamente del entorno del proceso hijo.
- El conector MCP no las lee, no las devuelve y no las nombra. Ninguna de sus diez herramientas se
  acerca.

---

## Qué hace

**Voz.** Te escucha en tu máquina con Whisper — el audio no sale de tu equipo para transcribirse.
Detecta cuándo empezás a hablar y cuándo terminás calibrándose contra el ruido de tu cuarto, no
contra un número fijo. Podés interrumpirlo mientras habla.

**Actúa.** Abre, cierra y trae al frente aplicaciones instaladas. Controla el volumen y lo que se
está reproduciendo. Crea, lee, mueve y borra archivos y carpetas (los borrados van a una papelera
propia de la que se pueden recuperar). Ejecuta comandos de PowerShell. Busca en la web.

**Ve y toca.** Saca capturas de pantalla y las manda al modelo. Lee los controles de una ventana
ajena por accesibilidad y hace clic en ellos por nombre, en vez de por coordenadas.

**Aprende.** Le podés enseñar reglas explícitas (*«que cada vez que te pida una canción, le des play
automáticamente»*) y quedan guardadas. Recuerda objetivos abiertos entre conversaciones.

**Se conecta.** Habla el protocolo MCP, así que le podés enchufar servidores externos —Spotify,
Google Drive, mail y calendario— y sus herramientas aparecen junto a las nativas. Si un servidor se
cae, se reconecta solo.

**Y se deja enchufar.** Trae su propio servidor MCP, así que se le puede agregar a Claude como
conector: ver y mover sus misiones, dejarle una pregunta, mirar tus proyectos. Con una frontera
escrita en el código —no aprueba memoria, no toca ninguna clave, no borra nada, y toda acción que
escribe consulta antes la política de permisos—. Ver [docs/CONECTOR.md](docs/CONECTOR.md).

**Conversa hablando.** Con una clave de Google usa Gemini Live: una sola conexión dúplex en lugar de
grabar, reconocer, pensar y sintetizar. El micrófono queda abierto mientras habla, así que
**hablándole encima se calla** — eso el camino de siempre no lo puede hacer por más que se lo apure,
porque ahí, mientras habla, no hay nadie escuchando. Sin clave anda igual, por el camino de siempre.

**Se mira sin mirarla.** El orbe tiene quince estados con transiciones que tienen forma propia, seis
registros de ánimo y diecinueve desplegables. Sin movimiento —si el sistema lo pide— los quince
colapsan a siete siluetas y la etiqueta deja de ser opcional.

**Frena.** Un atajo global corta todo en cualquier momento. Y silenciar cierra la sesión de voz, no
la calla nada más: la pantalla y el micrófono no pueden decir cosas distintas.

---

## Qué falta

Este proyecto está en desarrollo activo y hay cosas a medias. Las conocidas, sin maquillar:

- **Nada de la voz se probó contra una voz humana.** Los números del detector salen de voz
  sintetizada y de bancos: falta medir falsos positivos por hora en uso real, distintas voces y
  acentos, distancia y tipo de micrófono.
- **Las misiones, los permisos aprendidos y los objetivos están escritos y nunca se ejercitaron.**
  Se comprueba mirando la carpeta de datos: `misiones.json`, `autonomia.json` y `objetivos.json` no
  existen. El código está probado; el uso no.
- **Cambiarle el nombre sin volver a correr el instalador todavía no se puede.** El menú del botón
  derecho del orbe abre paneles, elige si te sigue entre pantallas y lo guarda en la bandeja; el de
  la bandeja silencia, elige la forma y lo arranca con Windows. Ninguno de los dos tiene dónde
  renombrarlo, y debería.
- **Escribir en un chat de Claude Code no se puede**, y está documentado por qué: el `.jsonl` de la
  sesión es el registro que escribe el proceso vivo y no un buzón, `claude -p --resume` arranca otro
  proceso en vez de hablarle a la que espera, y el CLI no trae ningún comando para eso. La
  herramienta te devuelve el mensaje armado para pegar, con el error puesto.
- **La transcripción tarda alrededor de 0,71× el largo del audio** con `ggml-small`; ése es hoy el
  piso de latencia del camino de siempre. El camino en vivo no lo tiene, pero cuesta plata por minuto.
- Quedan bordes sin ejercitar que piden hardware que no tenemos: dos monitores con escalas distintas,
  y un juego en pantalla completa exclusiva.

---

## Para desarrollar

```bash
dotnet build Viernes.slnx
```

```bash
dotnet test Viernes.slnx
```

Necesitás el SDK de .NET fijado en [`global.json`](global.json). La solución tiene cinco proyectos:

| Proyecto | Qué es |
|---|---|
| `Viernes.Core` | Conversación, herramientas, modelos, aprendizaje, misiones, sesión en vivo. Sin dependencias de Windows. |
| `Viernes.Platform.Windows` | Voz, acciones sobre el sistema, preferencias. |
| `Viernes.Memory` | Memoria personal persistente. |
| `Viernes.Mcp` | El conector: expone Viernes como servidor MCP. |
| `Viernes.App` | La ventana, el orbe y la bandeja. WPF. |

Casi todo lo que se puede probar sin Windows vive en `Viernes.Core`, y por eso ahí están casi todas
las pruebas. `Viernes.App` no tiene proyecto de pruebas —WPF no se deja— así que lo suyo se verifica
corriéndolo: `Viernes.exe --render-orb <carpeta>` saca dos hojas de contactos por cuerpo (los quince
estados y el movimiento) y `Viernes.exe --medir` abre el banco de micrófono.

Los comentarios del código están en castellano y explican **por qué**, no qué. Varios documentan
mediciones reales — si vas a cambiar un umbral, leelos primero: la mayoría existe porque el valor
obvio estaba mal.
