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
| `viernes_proyectos_listar` | Las sesiones de Claude Code: carpeta, rama, si trabajan o esperan, desde cuándo. Se puede acotar a un proyecto |
| `viernes_proyecto_escribir` | Hoy **no puede escribir**. Ver más abajo |

## Lo que el conector ve

Esto no es una lista de riesgos teóricos: es lo que sale por el tubo hacia el modelo del otro lado
cuando llama una herramienta. Está acá porque «lectura libre» es una decisión —leer no pide
permiso— y una decisión hay que poder leerla antes de tomarla.

| Sale siempre | En qué herramienta |
|---|---|
| Título, objetivo, contexto, bitácora entera y pregunta pendiente de cada misión | `viernes_misiones_listar`, `viernes_estado` |
| Todo lo que Viernes tiene anotado del usuario, confirmado y supuesto | `viernes_memoria_buscar` |
| Cuánto va gastado hoy y en el mes, en dólares | `viernes_estado` |
| **La ruta completa de la carpeta, la rama de git y el id de sesión de cada sesión de Claude Code de la máquina**, esté o no relacionada con lo que se está haciendo | `viernes_proyectos_listar`, `viernes_proyecto_escribir` |
| Si cada una de esas sesiones está trabajando, esperando o quieta, y desde hace cuánto | `viernes_proyectos_listar`, `viernes_estado` |

Lo de la tercera fila es lo que más sorprende y por eso está en negrita: en una corrida real salieron
las rutas de tres proyectos distintos del usuario, ninguno de ellos aquel sobre el que se estaba
trabajando. `viernes_estado` cuenta cuántas hay en cada estado, sin rutas ni texto.

| No sale, salvo que se pida por su nombre | Cómo se pide |
|---|---|
| Lo último que dijo el asistente en cada sesión, hasta 600 caracteres | `viernes_proyectos_listar` con `ultimo_mensaje=true` |

Eso último es contenido de conversaciones de **otros proyectos del usuario**. Salía por omisión y
ahora no: hay que pedirlo, y cuando se pide queda en el registro de la llamada quién lo pidió. En la
misma herramienta está `proyecto`, para mirar una carpeta en vez de toda la máquina — ver todas las
sesiones del equipo para saber si un proyecto espera es más de lo que hace falta.

Y lo que no ve, por si alguien lo da por hecho: no lee el contenido de los archivos de esos
proyectos, no lee el chat del usuario con Viernes, y no toca las claves (ver abajo).

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

Cada acción consulta por **lo que realmente va a hacer**, y no por el nombre de la herramienta que
la disparó. Cancelar una misión con motivo escribe dos cosas —una línea en la bitácora y el cierre—,
así que consulta las dos: «mision avanzar» para la línea y «mision cerrar» para el cierre. Con
«mision avanzar» en Nunca, la misión se cancela igual y la línea no entra; la respuesta lo dice, en
vez de dejar el permiso respetado en silencio, que se lee igual que ignorado.

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

## Funciona con el orbe abierto

Durante un tiempo no. `MissionBook` cacheaba el archivo y no invalidaba nunca, así que una misión
creada desde Claude no aparecía en el orbe hasta reiniciar, y peor: cuando la aplicación guardaba
cualquier otra cosa, reescribía el archivo entero con su copia vieja y **se perdía lo que había
escrito el conector**. Esta página decía que movieras misiones con el orbe cerrado.

Ya no hace falta, y el arreglo son dos piezas:

- el libro **relee cuando el archivo cambió** —compara fecha y tamaño, porque dos escrituras dentro
  del mismo tic del reloj comparten fecha—;
- y la compuerta del archivo es **estática por ruta**, así que dos instancias sobre el mismo
  `misiones.json` se excluyen de verdad. Con una compuerta por instancia, dos leer-modificar-escribir
  no se ven y el último que guarda pisa entero al otro: el archivo no se corrompe —el reemplazo es
  atómico— pero el trabajo del otro desaparece sin que nada avise.

Lo mismo se hizo con `autonomia.json`, donde el mismo defecto era peor: revocar un permiso desde el
desplegable no frenaba nada en el proceso en curso y después se borraba solo.

## Probarlo a mano

Con el servidor compilado, un saludo MCP por tubería alcanza:

```
dotnet build src\Viernes.Mcp -v q
```

y después mandarle por entrada estándar `initialize`, `notifications/initialized` y `tools/list`. Lo
que contesta hoy: protocolo `2025-06-18`, servidor `viernes`, **10 herramientas**.

Las pruebas viven en `tests/Viernes.Mcp.Tests` y corren sobre una carpeta temporal: ninguna toca las
misiones, la memoria ni los permisos reales.
