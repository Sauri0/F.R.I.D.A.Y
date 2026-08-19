# El conector: Viernes desde Claude

Un servidor MCP que habla por entrada y salida estándar y expone lo que Viernes ya sabe hacer, sobre
**los mismos archivos** que usa la aplicación (`%LOCALAPPDATA%\Viernes`). No reimplementa nada: usa
`MissionBook`, `JsonPersonalMemoryStore`, `ClaudeSessionWatcher` y `UsageLedger` tal como están. Lo
que Claude anota por acá lo ve el orbe, y al revés.

Vive en `src/Viernes.Mcp`. El ejecutable se llama `viernes-mcp`.

## Cómo se agrega

**Claude Code**, disponible en todos tus proyectos:

```
claude mcp add -s user viernes -- dotnet run --project N:\Viernes\src\Viernes.Mcp
```

`dotnet run` compila si hace falta y **no ensucia la salida estándar** —comprobado con una
recompilación forzada en el medio de un saludo MCP—, así que sirve tal cual. Si preferís que arranque
sin compilar nada, publicalo una vez:

```
dotnet publish N:\Viernes\src\Viernes.Mcp -c Release -o N:\Viernes\publicado\conector
claude mcp add -s user viernes -- N:\Viernes\publicado\conector\viernes-mcp.exe
```

**Aplicación de Claude**, en `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "viernes": {
      "command": "dotnet",
      "args": ["run", "--project", "N:\\Viernes\\src\\Viernes.Mcp"]
    }
  }
}
```

Para comprobar que quedó: `claude mcp list`.

## Qué trae

| Herramienta | Qué hace |
|---|---|
| `viernes_estado` | La foto de ahora: misiones abiertas, qué espera respuesta, memoria sin confirmar, sesiones de Claude Code y cuánto va gastado hoy y en el mes |
| `viernes_misiones_listar` | Las misiones abiertas con su objetivo, desde cuándo, último avance y pregunta pendiente |
| `viernes_mision_crear` | Anota un encargo que dura hasta cumplirse |
| `viernes_mision_avanzar` | Suma una línea a la bitácora |
| `viernes_mision_cerrar` | Terminada o cancelada, con el motivo escrito |
| `viernes_mision_preguntar` | Deja una pregunta que sobrevive al reinicio y aparece en el orbe |
| `viernes_memoria_buscar` | Lo que Viernes sabe del usuario, separando lo confirmado de lo supuesto |
| `viernes_memoria_proponer` | Deja un dato **pendiente de aprobación**. No lo aprueba |
| `viernes_proyectos_listar` | Las sesiones de Claude Code: carpeta, rama, si trabajan o esperan, desde cuándo |
| `viernes_proyecto_escribir` | Hoy **no puede escribir**. Ver más abajo |

## Lo que no hace, y es a propósito

No son funciones que falten. Están escritas en `src/Viernes.Mcp/ConnectorBoundary.cs` con el motivo,
porque el próximo que agregue una herramienta va a querer «completar la API».

1. **No aprueba memoria.** Propone y la propuesta queda esperando al usuario, que decide en Viernes.
   Si el conector pudiera aprobar, cualquier cosa que Claude dedujera en una charla se volvería un
   hecho sobre el usuario sin que él se entere.
2. **No pasa por encima de `AutonomyPolicy`.** Toda acción que escribe algo consulta primero los
   permisos; si la política dice «preguntar», el conector **no la hace** y devuelve por qué —como un
   error de MCP, no como texto—. Conectar un servidor no puede ser la forma de saltearse los
   permisos que el usuario configuró: si lo fuera, la política valdría hasta que alguien agregue un
   conector.
3. **No toca las claves.** Ninguna herramienta lee, devuelve ni nombra la clave de Google ni la de
   OpenRouter. Por eso el conector nunca construye `ViernesOptions`, que es quien las resuelve del
   entorno.
4. **No borra nada de forma irreversible.** Cerrar una misión la deja cerrada con su bitácora. No hay
   herramienta de olvidar.

Escribirle a otra sesión de Claude Code pide autorización desde el primer día sin que nadie
configure nada: la acción se presenta ante la política como «enviar mensaje a claude code», y
«enviar» ya está en la lista de lo que sale del equipo.

## Escribir en el chat de Claude Code: por qué todavía no

Se buscó una forma soportada y **no la hay**. Lo que existe, y por qué cada camino se descartó:

- **El archivo de la sesión** (`%USERPROFILE%\.claude\projects\<proyecto>\<id>.jsonl`) es el registro
  que escribe el proceso vivo, no un buzón. Agregarle una línea a mano es escribir en el archivo
  abierto de otra aplicación, y además no serviría: el proceso que está esperando tiene la
  conversación en memoria y no vuelve a leerlo.
- **`claude -p --resume <id>`** existe y está soportado, pero no le habla a la sesión que está
  esperando: arranca *otro* proceso sobre la misma conversación. Dos escritores sobre el mismo
  registro, un turno que gasta plata del usuario sin que él lo vea, y herramientas corriendo con los
  permisos de ese proceso.
- **El registro de sesiones vivas** (`%USERPROFILE%\.claude\sessions\<pid>.json`) anota proceso, id de
  sesión y carpeta, pero no publica ningún canal: ni puerto ni tubería. Sirve para saber que la
  sesión está viva, no para hablarle. Es además un archivo interno sin formato documentado.
- **Teclear en la ventana** con automatización de escritorio escribe a ciegas en lo que tenga el
  foco. Anda en la demostración y arruina un trabajo real.
- En `claude --help` no hay ningún comando para mandarle un mensaje a una sesión en curso (revisado
  contra Claude Code 2.1.126).

Mientras tanto, `viernes_proyecto_escribir` hace lo único útil y honesto: identifica a qué sesión
iba —carpeta, rama, qué dijo último— y devuelve el mensaje armado para que lo pegue el usuario, con
`isError` puesto para que del otro lado no se lea como si hubiera pasado algo. El motivo entero vive
en `src/Viernes.Core/Projects/ClaudeSessionWriter.cs`; si algún día aparece un canal soportado, hay
que cambiar un solo lugar.

Si lo que hace falta es que al usuario le quede algo anotado, eso sí funciona:
`viernes_mision_preguntar` deja la pregunta viva en la misión y el orbe la muestra.

## Una limitación que hay que saber

`MissionBook` **cachea el archivo de misiones en memoria y no invalida nunca**: lo lee una vez y
después devuelve la lista que tiene. La aplicación ya lo dice de sí misma en `AssistantRuntime`: «lo
que no se ve es un `misiones.json` editado por fuera con Viernes abierto; eso pide reiniciar».

El conector es, justamente, un editor de afuera. Con Viernes abierto:

- una misión creada desde Claude no aparece en el orbe hasta reiniciar la aplicación;
- y si la aplicación guarda después —al anotar cualquier otra cosa—, reescribe el archivo entero con
  su copia vieja y **se pierde lo que escribió el conector**.

Con Viernes cerrado no pasa nada de esto. Arreglarlo es cambiar `MissionBook` —releer si el archivo
cambió, o guardar fusionando—, que está fuera de lo que toca el conector y hay que hacerlo aparte.
Mientras tanto: si vas a mover misiones desde Claude, hacelo con el orbe cerrado.

## Probarlo a mano

Con el servidor compilado, un saludo MCP por tubería alcanza:

```
dotnet build src\Viernes.Mcp -v q
```

y después mandarle por entrada estándar `initialize`, `notifications/initialized` y `tools/list`. Lo
que contesta hoy: protocolo `2025-06-18`, servidor `viernes`, **10 herramientas**.

Las pruebas viven en `tests/Viernes.Mcp.Tests` y corren sobre una carpeta temporal: ninguna toca las
misiones, la memoria ni los permisos reales.
