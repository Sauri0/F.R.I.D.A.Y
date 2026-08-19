# Instalar, actualizar y cambiarle el nombre

Todo lo que sigue lo hace un solo archivo: `instalador/instalar.ps1`, que se ejecuta con doble clic
en `instalador/INSTALAR.cmd`. El mismo archivo instala de cero y actualiza; no hay dos instaladores.

---

## Dónde vive cada cosa

```
%LOCALAPPDATA%\Viernes\
├─ app\                     la aplicación: Viernes.exe, viernes-mcp.exe y el .NET que traen adentro
├─ version.txt              la etiqueta de la versión instalada
├─ settings.json            el nombre elegido, la forma del orbe, mute y el resto de preferencias
├─ claves.json              la clave de Google (opcional) y sus instrucciones
├─ memory.json              la memoria personal
├─ reglas.json              las reglas que le enseñaste
├─ aprendido.json           qué acciones funcionaron antes
├─ autonomia.json           hasta dónde puede llegar sola
├─ misiones.json            los encargos que sobreviven a la charla
├─ objetivos.json           los objetivos abiertos
├─ assistant-data.json      recordatorios y agenda
├─ usage-ledger.json        cuánto se gastó, sin contenido de las conversaciones
├─ servidores-mcp.json      los servidores MCP declarados
├─ window.json              dónde dejaste el orbe
├─ monitores.json           en qué pantalla estaba
├─ papelera\                lo que borró la herramienta de archivos, recuperable
├─ mcp\                     lo que dejan los servidores MCP conectados
├─ trace.log                la bitácora: qué pasó y cuándo, sin audio ni claves ni el texto de
│                           las conversaciones. Es el archivo que conviene mandar si algo falla
└─ Models\
   ├─ Whisper\ggml-small.bin      el oído, 465 MB
   └─ Vad\                        el detector de voz, unos 16 MB
```

**La carpeta se llama `Viernes` aunque tu asistente se llame otra cosa.** Identifica al producto, no
al asistente: si siguiera al nombre, renombrarlo abandonaría el historial, las preferencias y el
modelo de voz ya descargado.

La clave de OpenRouter no está en esta lista y no está en ningún archivo: vive en las variables de
entorno de tu cuenta de Windows.

---

## La primera vez

En orden, y con las preguntas primero para que no haya que quedarse mirando una barra de descarga
para poder contestar:

1. **Comprueba el equipo.** Windows 10 o posterior, 64 bits. Si lo estás corriendo como
   administrador te avisa que no hace falta: todo va a tu carpeta de usuario, y elevar sólo deja la
   instalación con permisos que después no vas a poder tocar.
2. **Pregunta el nombre** y te muestra ahí mismo con qué frases lo vas a despertar.
3. **Pide la clave de OpenRouter.** Se escribe a ciegas, no se muestra y no se registra. Va a las
   variables de entorno de tu cuenta. De paso deja un tope de gasto de USD 2 por día en
   `VIERNES_OPENROUTER_DAILY_BUDGET_USD`, si no había ninguno. Hasta hoy el instalador escribía
   `VIERNES_DAILY_BUDGET`, que no la lee nadie: el tope se anunciaba y no frenaba nada.
4. **Pide la clave de Google, que es opcional.** Enter y sigue de largo: crea igual el archivo
   `claves.json` con las instrucciones adentro para que la pegues cuando quieras.
5. **Baja la aplicación** desde el último release de GitHub.
6. **Baja el modelo de voz** (465 MB) y el detector.
7. **Guarda el nombre, crea los accesos** en el escritorio y en el menú Inicio, y ofrece arrancar con
   Windows.

---

## Actualizar

**Volvé a correr `INSTALAR.cmd`.** Cuantas veces haga falta.

### Qué se reemplaza

La carpeta `app\` entera, y nada más. Ahí adentro está todo lo que cambia entre versiones: el orbe y
sus estados, las funciones, las herramientas, el conector, el runtime de .NET. No se parchea nada:
se baja el paquete nuevo y la carpeta vieja se va.

### Qué se conserva

Todo lo demás de la lista de arriba. De esos archivos, la corrida completa toca dos y ninguno pierde
nada: `settings.json`, que se lee, se le confirma el nombre y se vuelve a escribir con el resto de
las preferencias como estaban; y `claves.json`, sólo si le decís que reemplace la clave de Google, y
editándolo en vez de reescribirlo.

Y en particular:

- **El modelo de voz no se vuelve a bajar.** Si ya está y pesa lo que tiene que pesar, lo saltea. Lo
  mismo el detector.
- **Las claves no se pisan en silencio.** Si ya tenés la de OpenRouter, te dice que la tenés y sólo
  la reemplaza si contestás que sí. Con la de Google hace lo mismo, y cuando la guarda edita el
  archivo en vez de reescribirlo, así no se lleva puestas tus notas ni `VIERNES_LIVE`.
- **Si ya tenés la última versión, no baja nada** y te lo dice.

### Si algo se corta a la mitad

No queda una instalación rota. El paquete se extrae en `app.nueva`, se comprueba que adentro esté
`Viernes.exe`, y recién ahí la carpeta vieja se borra y la nueva ocupa su lugar. Si se corta la luz
antes de ese momento, lo que tenías sigue funcionando. Volvé a correr el instalador cuando quieras.

Las descargas hacen lo mismo con un `.parcial` que sólo pasa a ser el archivo bueno cuando terminó.

### La puerta rápida

Desde una terminal:

```
INSTALAR.cmd -Actualizar
```

Sólo actualiza la aplicación y la abre. No pregunta nada: ni el nombre, ni las claves, ni el arranque
con Windows. **No sirve para instalar de cero** —no crea accesos ni baja el modelo de voz—, es para
cuando ya lo tenés andando y sólo querés la versión nueva.

### Qué versión tengo

`%LOCALAPPDATA%\Viernes\version.txt` tiene la etiqueta de lo que está instalado; lo escribe el
instalador cuando termina de mover la carpeta nueva a su lugar.

Los paquetes armados de acá en adelante traen además su propio `version.txt` adentro de `app\`, así
que una carpeta copiada o restaurada de una copia de seguridad se puede identificar sola. Quien
decide si hay algo nuevo sigue siendo el de afuera, que es el que el instalador escribe.

---

## Cambiarle el nombre

**Desde la aplicación: clic derecho sobre el orbe → «Cómo me llamo…»**, o desde la bandeja, que es la
única puerta cuando el orbe está guardado. Mientras escribís te muestra cómo va a quedar el nombre y
con qué frases lo vas a despertar; Aceptar queda deshabilitado hasta que el nombre sirva, con el
motivo escrito debajo.

Al aceptar cambia todo sin reiniciar: se guarda en `settings.json`, se rehace la primera línea del
prompt sin perder el hilo de la charla, y el oído se cierra y se vuelve a abrir con las frases
nuevas. Si algo de eso no sale —el micrófono lo tomó otro programa y el oído no volvió, hay una
charla hablada abierta que sigue con el nombre anterior, el archivo no se pudo escribir— **te lo
dice**, en vez de dar el renombrado por hecho. Y si reintentás con el mismo nombre, vuelve a
intentar lo que quedó pendiente en lugar de contestar que ya está.

También se puede desde el instalador, con `INSTALAR.cmd -Reconfigurar`. Ése cierra la aplicación
antes de escribir: si estuviera abierta, seguiría con su copia de las preferencias en memoria y
pisaría el archivo con el nombre viejo en cuanto tocaras cualquier opción de la bandeja.

De ese nombre sale todo lo demás —la primera línea del prompt, el título de la ventana, la bandeja,
el acceso directo y las frases con las que lo despertás—.

**Cambiar el nombre cambia cómo se lo llama.** Si pasa de Ana a Nina, deja de contestar a *«Hola
Ana»* y contesta a *«Hola Nina»*. Eso funciona porque las frases no se guardan: se derivan del nombre
cada vez que arranca. Renombrar incluso borra la entrada `wakeWordPhrases` del archivo de
preferencias al renombrar, porque una lista escrita a mano ahí congelaría las frases del nombre
anterior.

Las preferencias que no tienen que ver con el nombre —la forma del orbe, el mute, el arranque con
Windows— se respetan: el archivo se lee, se le cambia el nombre y se vuelve a escribir.

Los accesos directos viejos se borran y se crean de nuevo con el nombre nuevo, así que no te quedan
dos íconos.

### Las reglas del nombre

De 2 a 24 caracteres, letras, espacios, guiones y apóstrofos. Sin números: el nombre se dice en voz
alta y los números no se reconocen bien. Las mayúsculas que escribas se respetan —`JARVIS` y `McCoy`
quedan como los escribiste—; sólo se sube la primera letra de cada palabra si venía en minúscula.

Son las mismas reglas que aplica la aplicación, escritas dos veces —el instalador corre antes de que
la aplicación exista en el disco, así que no hay forma de compartirlas—. Para comprobar que no se
separaron, el instalador acepta `-ProbarNombre <nombre>`: juzga ese nombre, imprime el veredicto y la
forma final, y sale sin instalar nada. Tiene que coincidir con lo que devuelve `AssistantIdentity`
para el mismo nombre. Si se separan, el instalador acepta nombres que la aplicación después guarda
distinto, y el usuario ve que su asistente se llama otra cosa.

### Por qué son dos palabras

Porque el nombre suelto no se puede separar del ruido. Con el nombre original, *«el viernes tengo
turno»* dicho al pasar despertaba al asistente con más confianza que casi todas las llamadas de
verdad: ningún umbral cortaba una cosa sin cortar la otra. *«Hola Ana»* no aparece sola en una
conversación ajena. Está contado con los números en
[`AssistantIdentity.WakePhrases`](../src/Viernes.Core/Configuration/AssistantIdentity.cs) y en
[VOICE.md](VOICE.md).

Nombrarla al final igual funciona —*«creame una carpeta, che Ana»*—: lo dicho antes se recupera de
una ventana rodante de diez segundos.

Si querés otras frases, incluido el nombre suelto, están en `VIERNES_WAKE_PHRASES`, separadas por
punto y coma, hasta ocho.

---

## Si algo falla

**«El repositorio no está accesible».** El instalador le pregunta a la API de GitHub cuál es el
último release. Un 404 ahí casi siempre es un repositorio privado visto sin credenciales —GitHub
contesta lo mismo en los dos casos a propósito—, no un problema de red.

**«El paquete descargado no trae Viernes.exe».** El paquete publicado no tiene la forma que el
instalador espera. Nada se instaló: la carpeta anterior sigue entera.

**«La descarga del modelo quedó incompleta».** Volvé a correrlo. El archivo a medias ya se borró.

**No tenés PowerShell 7.** No hace falta. `INSTALAR.cmd` usa `pwsh` si está y si no cae a
`powershell`, el 5.1 que trae Windows de fábrica. Los dos andan.

**Windows bloquea el script.** No hace falta cambiar nada del equipo: el `.cmd` levanta la política
de ejecución sólo para ese proceso.

---

## Cómo se arma el paquete

`.github/workflows/release.yml` corre al empujar una etiqueta `v*`. Prueba, publica la aplicación y
el conector en la misma carpeta —comparten el runtime, que es lo que pesa— y sube un zip llamado
`Viernes-<etiqueta>-win-x64.zip`.

El contrato entre ese flujo y el instalador es corto, y romperlo deja a todo el mundo sin poder
actualizar:

1. El release tiene que traer **un asset cuyo nombre contenga `win-x64` y termine en `.zip`**. Es lo
   que busca `instalar.ps1`.
2. Adentro del zip, en la raíz, tiene que estar **`Viernes.exe`**. El instalador lo comprueba antes
   de reemplazar nada.

Si cambia una de las dos cosas, hay que cambiar la otra en el mismo commit.
