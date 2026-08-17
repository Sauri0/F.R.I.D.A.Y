# F.R.I.D.A.Y

Un asistente de escritorio para Windows que vive en una gota de vidrio, escucha cuando lo llamás y
hace cosas en tu máquina: abre aplicaciones, maneja archivos, pone música, mira la pantalla, busca en
la web y aprende cómo trabajás.

Habla en rioplatense. Vos elegís cómo se llama.

---

## Instalación

1. Bajá la carpeta [`instalador/`](instalador/) — necesitás `INSTALAR.cmd` e `instalar.ps1` juntos.
2. Doble clic en **`INSTALAR.cmd`**.
3. Contestá dos preguntas: cómo se va a llamar y cuál es tu clave de OpenRouter.

El instalador se encarga del resto: baja la aplicación, baja el modelo de voz, crea los accesos
directos y lo deja andando. **No hace falta instalar .NET** — viene adentro del paquete.

Volver a correr `INSTALAR.cmd` actualiza a la última versión sin volver a preguntar nada.

### Qué necesitás

| | |
|---|---|
| Sistema | Windows 10 o posterior, 64 bits |
| Espacio | ~700 MB (200 la aplicación, 490 el modelo de voz) |
| Micrófono | Cualquiera |
| Clave | Una de [OpenRouter](https://openrouter.ai/keys), gratis de sacar |

---

## El nombre lo elegís vos

El instalador pregunta cómo se va a llamar. Ese nombre entra en la primera línea de su prompt, en el
título de la ventana, en la bandeja del sistema, y define cómo lo despertás.

**Siempre son dos palabras.** Si lo llamás Ana, lo despertás diciendo *«Hola Ana»*, *«Che Ana»* o
*«Ey Ana»* — nunca *«Ana»* a secas. No es un capricho: con el nombre original, la palabra suelta
dicha al pasar (*«el viernes tengo turno»*) lo despertaba con confianza 0,69, mientras las
activaciones verdaderas puntuaban entre 0,62 y 0,68. El falso positivo puntuaba **más alto** que casi
todos los aciertos, así que ningún umbral los separaba. Dos palabras resuelven el problema de raíz, y
sirven para cualquier nombre.

Para cambiarlo después, volvé a correr el instalador.

---

## Tu clave es tuya

**Este repositorio no contiene ninguna clave, y nunca va a contener una.**

La única credencial que usa el asistente es `OPENROUTER_API_KEY`. La ponés vos en la instalación y se
guarda como variable de entorno de tu cuenta de Windows:

- No se escribe en ningún archivo del proyecto.
- No se manda a ningún lado que no sea OpenRouter.
- No viaja a los procesos que el asistente ejecuta por vos: cuando corre un comando de PowerShell,
  la borra explícitamente del entorno del proceso hijo.

Todos los modelos pasan por OpenRouter. No hay ninguna otra API involucrada.

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
Google Drive, lo que exista— y sus herramientas aparecen junto a las nativas.

**Frena.** Un atajo global corta todo en cualquier momento.

---

## Qué falta

Este proyecto está en desarrollo activo y hay cosas a medias. Las conocidas, sin maquillar:

- La memoria de recetas guarda transcripciones crudas como clave, así que casi nunca vuelve a
  encontrar lo que aprendió: de 58 recetas guardadas, 56 se usaron una sola vez.
- Los objetivos persistentes se escriben pero el archivo nunca llegó a crearse en uso real.
- La transcripción tarda alrededor de 0,85× el largo del audio; ése es hoy el piso de latencia.

---

## Para desarrollar

```bash
dotnet build Viernes.slnx
```

```bash
dotnet test Viernes.slnx
```

Necesitás el SDK de .NET fijado en [`global.json`](global.json). La solución tiene cuatro proyectos:

| Proyecto | Qué es |
|---|---|
| `Viernes.Core` | Conversación, herramientas, modelos, aprendizaje. Sin dependencias de Windows. |
| `Viernes.Platform.Windows` | Voz, acciones sobre el sistema, preferencias. |
| `Viernes.Memory` | Memoria personal persistente. |
| `Viernes.App` | La ventana, la gota y la bandeja. WPF. |

Los comentarios del código están en castellano y explican **por qué**, no qué. Varios documentan
mediciones reales — si vas a cambiar un umbral, leelos primero: la mayoría existe porque el valor
obvio estaba mal.
