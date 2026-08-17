# Capacidades y servidores

## Ver la pantalla y operar el cursor

Viene de fábrica, sin servidores ni binarios de terceros: captura nativa por GDI y codificación con
el PNG que ya trae el framework.

| Acción | Qué hace |
|---|---|
| `see_screen` | Le muestra la pantalla al modelo (`target: "ventana"` para sólo la de adelante) |
| `move_cursor`, `click`, `double_click`, `right_click` | `target: "x,y"` leído de la captura |
| `type_text` | Escribe el texto tal cual, con tildes y ñ |
| `press_key` | enter, tab, escape, suprimir, flechas, f5… |
| `scroll` | `target: "arriba"` o `"abajo"` |

Dos detalles que importan:

- La captura se **reduce a 1280 de ancho** antes de mandarla. Una pantalla entera son millones de
  píxeles que se cobran como tokens en cada turno; a 1280 el modelo sigue leyendo botones y texto.
  Medido: 455 ms para capturar, ~1.100 tokens de entrada.
- Las coordenadas se leen sobre la imagen reducida y **el código las traduce** a pantalla real. No se
  le pide al modelo que multiplique: en un monitor 1920×1080 la escala es 1,5 y cada clic caería a
  dos tercios de camino del objetivo.
- Con varios monitores se captura el principal. Para lo que esté en otro, `see_screen` con
  `"ventana"` toma la ventana de adelante esté donde esté.

# Conectar servidores MCP

Viernes es cliente MCP. Cada servidor que le conectes le suma capacidades sin tocar su código.

**El modelo sigue siendo el de OpenRouter.** MCP no aporta inteligencia: aporta manos. El modelo
decide, MCP ejecuta.

## Dónde va la configuración

```
%LOCALAPPDATA%\Viernes\servidores-mcp.json
```

Si el archivo no existe, Viernes funciona igual con sus capacidades de fábrica.

## Las credenciales no van en el archivo

`environment` mapea **nombre de variable del proceso hijo → nombre de la variable de tu sistema**.
El archivo guarda nombres, nunca valores.

```json
"environment": { "ALGUNA_API_KEY": "VIERNES_ALGUNA_API_KEY" }
```

Y la variable se define una sola vez:

```bash
setx VIERNES_ALGUNA_API_KEY "el-valor"
```

Ojo: no todos los servidores leen variables de entorno. El de Spotify, por ejemplo, guarda sus
credenciales en un archivo propio (ver abajo).

## Los dos que quedaron listos para habilitar

El archivo ya está creado en `%LOCALAPPDATA%\Viernes\servidores-mcp.json`, con ambos en
`"enabled": false` porque cada uno necesita un paso que sólo podés dar vos.

### windows — operar cualquier aplicación

Encuentra los controles **por nombre**, no por coordenadas, así que sobrevive a cambios de
resolución, tema y posición de ventana. Es un ejecutable .NET 10 suelto: no usa npm ni Python.

1. Bajá el binario de [Releases](https://github.com/sbroenne/mcp-windows/releases)
2. Ponelo en `%LOCALAPPDATA%\Viernes\mcp\`
3. Poné `"enabled": true`

No lo bajé yo a propósito: descargar y ejecutar un binario de terceros en tu máquina es una decisión
tuya, no mía.

### spotify — reproducir de verdad, colas, playlists

Este se compila desde el repositorio y su OAuth abre el navegador con tu cuenta, así que los dos
pasos son necesariamente tuyos.

```bash
git clone https://github.com/marcelmarais/spotify-mcp-server.git
```

Después, adentro de esa carpeta: `npm install && npm run build`, creás `spotify-config.json` con el
client id y secret de tu app de [Spotify for Developers](https://developer.spotify.com/dashboard)
—es gratis—, corrés `npm run auth` para autorizar, y ponés `"enabled": true`.

## Lo que hay que tener instalado

Depende del servidor. Verificado en este equipo: **Node 24** y **Python 3.14**. El de Windows no
necesita ninguno de los dos.

## Permisos

Toda herramienta que llegue por MCP se registra como `RequiresConfirmation`. Es a propósito: un
servidor MCP es un proceso ajeno que puede hacer cosas reales, y lo que Viernes lee de la web podría
intentar dispararlo. Confirmás por voz — «dale» o «no».

## Costo de arranque

Levantar un servidor tarda. Medido con `npx`: casi 12 segundos la primera vez —descarga el
paquete— y 1,8 segundos con la caché tibia. Conviene levantarlos al iniciar y no al primer pedido.
