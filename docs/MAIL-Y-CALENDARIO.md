# Conectar tus mails y calendarios

Es el único paso de todo el proyecto que no puedo hacer por vos: Google necesita que **vos** autorices
el acceso a tus casillas, con tu contraseña y tu segundo factor.

Toma una tarde, una sola vez, y sirve para **todas** tus cuentas.

---

## Antes que nada: el servidor que NO hay que usar

Si buscás «Gmail MCP» vas a encontrar primero `@gongrzhe/server-gmail-autoauth-mcp`. Tiene 138.000
descargas por mes y aparece en todos los tutoriales.

**Está archivado desde agosto de 2025** y es de una sola cuenta. Las descargas son inercia de guías
viejas. No lo instales.

---

## Lo que sí: `workspace-mcp`

Un solo servidor hace **mail y calendario**, con **varias cuentas a la vez**. Cada herramienta lleva
la dirección como parámetro, así que no hace falta un proceso por casilla: una instancia atiende
todas.

```bash
winget install astral-sh.uv
```

```bash
uvx workspace-mcp --tools gmail calendar
```

Trae 14 herramientas de Gmail —buscar, leer, hilos, adjuntos, etiquetas, filtros, borradores,
enviar— y 7 de Calendar. Con `--tools gmail calendar` quedan 21 en vez de 120, que es lo que
conviene: cada herramienta ocupa lugar en el contexto de cada turno.

> Si alguna dependencia rompe con Python 3.14, forzá una versión conocida:
> `uvx --python 3.12 workspace-mcp --tools gmail calendar`

---

## El trámite de Google, paso a paso

**Un solo proyecto y un solo cliente OAuth sirven para todas tus cuentas.** Sólo repetís el
consentimiento una vez por casilla.

1. Entrá a [console.cloud.google.com](https://console.cloud.google.com) y creá un proyecto —por
   ejemplo `viernes-asistente`.
2. **APIs y servicios → Biblioteca**: habilitá **Gmail API** y **Google Calendar API**, una por una.
3. **Google Auth Platform → Branding**: nombre de la app, mail de soporte, mail del desarrollador.
   *(En 2025 renombraron esta sección: los tutoriales que hablan de «OAuth consent screen» describen
   una pantalla que ya no existe.)*
4. **Audience → External.** Es la única opción con una cuenta `@gmail.com`.
5. **Data Access → Add scopes**, y agregá:
   ```
   gmail.readonly   gmail.send      gmail.compose
   gmail.modify     gmail.labels    gmail.settings.basic
   calendar         calendar.events
   userinfo.email   userinfo.profile
   ```
6. **Audience → Test users**: agregá **todas** las direcciones que vaya a manejar.
7. **Credentials → Create credentials → OAuth client ID → Desktop app.** Descargá el JSON.
8. **Publicá la app** (*Audience → Publish app*). **Hacelo antes de autorizar**, ver la trampa de
   abajo.
9. Corré el servidor y autorizá **una cuenta por vez**. Se abre el navegador; Windows va a pedir
   permiso de firewall la primera vez porque el servidor escucha en localhost. Dale *Permitir*.

---

## Las dos trampas que te arruinan el fin de semana

### En modo «Testing» todo se muere a los 7 días

Google revoca las autorizaciones de los usuarios de prueba **siete días después del consentimiento**,
y con ellas el refresh token. Traducción: Viernes deja de leer el mail cada lunes.

**Se arregla publicando la app.** A cambio vas a ver una pantalla de *«Google no verificó esta
aplicación»* cada vez que autorices una cuenta nueva → *Configuración avanzada* → *Ir a la app*. Es
cosmético.

No necesitás verificación de Google ni auditoría de seguridad: hay una excepción explícita para
aplicaciones de uso personal, y sos el único usuario.

### Con Outlook personal, la sesión muere en una hora

Si además conectás Microsoft 365 con una cuenta `outlook.com` o `hotmail.com`, poné
`MS365_MCP_TENANT_ID=consumers`. Desde junio de 2026 los tokens emitidos con la autoridad por defecto
son rechazados en el primer refresco.

---

## Dónde quedan las credenciales

`%USERPROFILE%\.google_workspace_mcp\credentials\<tu-mail>.json` — un archivo por cuenta.

**Ese archivo contiene un refresh token en texto plano, y un refresh token es acceso completo a tu
casilla sin contraseña y sin segundo factor.** Es lo más sensible de todo el sistema.

- No lo pongas en OneDrive, Drive ni ninguna carpeta que se sincronice.
- No lo incluyas en un backup sin cifrar.
- No hace falta respaldarlo: si lo perdés, volvés a autorizar en un minuto.
- Si algo pasa: [myaccount.google.com/permissions](https://myaccount.google.com/permissions) revoca
  todas las cuentas de una.

---

## Cómo declararlo en Viernes

En `%LOCALAPPDATA%\Viernes\servidores-mcp.json`:

```json
{
  "name": "google",
  "command": "uvx",
  "arguments": ["workspace-mcp", "--tools", "gmail", "calendar"],
  "environment": {
    "GOOGLE_OAUTH_CLIENT_ID": "GOOGLE_OAUTH_CLIENT_ID",
    "GOOGLE_OAUTH_CLIENT_SECRET": "GOOGLE_OAUTH_CLIENT_SECRET"
  },
  "enabled": true
}
```

El bloque `environment` mapea **nombre de variable a nombre de variable**: los valores salen del
entorno de tu cuenta de Windows y nunca se escriben en este archivo. Ponelos una vez con:

```bash
setx GOOGLE_OAUTH_CLIENT_ID "tu-client-id"
```

---

## Qué va a poder hacer, y qué te va a preguntar

Desde el primer día, **sin preguntarte nunca**: leer, buscar, clasificar, etiquetar y **dejar
borradores**. Pedir permiso para leer un mail convertiría al asistente en un trámite.

**Enviar siempre pregunta**, hasta que le digas lo contrario. Y se lo decís hablando, caso por caso:

> «A Ana contestale sola.»
> «Los de facturación siempre preguntame.»
> «A mi jefe nunca le escribas sin mostrarme.»

Eso queda guardado en `autonomia.json` y vale de ahí en adelante. Lo específico gana sobre lo
general —una regla sobre una persona pesa más que una sobre la acción— y un «nunca» gana sobre
cualquier permiso anterior, siempre.

Podés preguntarle en cualquier momento qué permisos tiene guardados.

---

## Si no querés hacer el trámite

Existe IMAP con contraseña de aplicación: cero trámite, funciona con cualquier proveedor. Perdés las
etiquetas de Gmail, la búsqueda con `from:` y `newer_than:`, los hilos y los borradores con semántica
Gmail — o sea, casi todo lo que hace falta para clasificar bien.

Y no es más seguro: una contraseña de aplicación da acceso total a la casilla, sin permisos
acotados, y sólo se revoca cambiando tu contraseña principal.

Sirve como plan B para casillas que no sean de Google ni de Microsoft.
