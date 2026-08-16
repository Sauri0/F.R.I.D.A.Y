# Viernes — brief de diseño

> Documento autocontenido para trabajar el diseño de Viernes en una sesión aparte.
> No hace falta ver el código: todo lo que necesitás está acá.

---

## 1. Qué es Viernes

Un **asistente personal nativo de Windows** para uso personal, en **español rioplatense**.
No es una web app ni una ventana de chat: es un **orbe que vive sobre el escritorio**, encima de todo,
todo el día. Casi nunca ocupa espacio; se expande sólo cuando hace falta y se retrae solo.

**Tesis del proyecto, y esto gobierna todo lo demás:**

> El modelo propone; el código local decide.

Un modelo más capaz no gana permisos. Nada irreversible. Nada oculto. Todo revocable.
Esto no es marketing: está implementado y es lo que hay que respetar al diseñar.

**Tono:** sereno, preciso, cálido. Proactivo sin invadir. El usuario siempre al mando.

---

## 2. La dirección elegida: «Gota»

De cinco direcciones exploradas (Halo, Iris, Cardumen, Onda, Gota) se eligió **Gota**.

**Qué la define:** no es un widget, es *algo que descansa ahí*. Sin bordes duros, sin geometría fija.

**Aprendizaje caro que ya pagamos —no lo repitas:** al principio se intentó hacerla «más líquida»
**deformándola más**. El resultado fue una ameba. Una gota en reposo es **casi esférica** por tensión
superficial. Lo que la hace leer como agua **no es la silueta, es cómo se comporta la luz adentro**.

Lo que efectivamente funcionó:

| Elemento | Por qué |
|---|---|
| **Borde oscuro y saturado** en el gradiente radial | Da densidad. Sin él es una mancha de color plana por más reflejo que tenga encima |
| **Reflejo especular chico, brillante y contenido** dentro del contorno | Es la diferencia entre algo mojado y una mancha |
| **Rebote de luz** abajo a la derecha, **teñido del color de estado** | En blanco puro leía como suciedad gris |
| **Punto de luz secundario difuso** arriba a la derecha | Confirma la curvatura. Si es un disco sólido parece una pastilla pegada |
| **Excursiones de ~3 px** sobre un radio de 25 | Viva de cerca, redonda de un vistazo |
| **Base ovoide 1.035 × 0.965** | Una gota apoyada es más ancha que alta |
| **Los reflejos NO se transforman con la masa** | La luz viene del ambiente, no del objeto. Si giran con el cuerpo, se despegan del borde |

**Períodos de animación deliberadamente no conmensurables** (6.3, 7.1, 8.7, 7.9, 9.3, 6.7, 8.1, 10.3,
7.5, 9.9, 8.3 segundos) para que el conjunto nunca vuelva a alinearse y no se perciba el loop.

**Nada gira.** Se probó rotar 360° en «pensando» y barre la silueta de un cuerpo ovoide, dejando los
reflejos flotando fuera del borde. Se eliminó.

---

## 3. El idioma de color — la constante

**Esto no se negocia y es lo único que no se puede mezclar entre direcciones.** El color del estado
es constante; la forma es la variable.

| Estado | Cuerpo | Profundidad | Borde | Cuándo |
|---|---|---|---|---|
| **Reposo** | `#72D9FF` cian | `#17607F` | `#08314A` | Nada pasando |
| **Escuchando** | `#72F0C0` verde | `#166B54` | `#07362A` | Micrófono tomando audio |
| **Pensando** | `#9BB7FF` periwinkle | `#304486` | `#151E45` | Turno en curso |
| **Hablando** | `#FFCE82` ámbar | `#7D541C` | `#3E2808` | Sintetizando voz |
| **Revisar** | `#FFB347` ámbar fuerte | `#7D4D13` | `#3E2304` | Espera confirmación / recordatorio |
| **Error** | `#FF7385` rojo | `#78263 2` | `#3B0F17` | Algo falló |

**Los estados cambian la viscosidad —la velocidad del mismo fluido— y el color. Nunca el vocabulario
de formas.** Multiplicadores actuales: reposo ×1, escuchando ×2.6, pensando ×3.4, hablando ×4.5,
revisar ×1.7.

**Superficie e interfaz (fuera del orbe):**

```
Fondo burbuja    gradiente #1A242E → #202832
Borde burbuja    rgba(114,217,255,0.28)
Texto primario   #F4F8FB
Texto secundario #A7B7C7
Texto tenue      #62758A
Acento           #72D9FF
Advertencia      #FFC56B
Peligro          #FF7385
Tipografía       Segoe UI Variable Text / Segoe UI
Monoespaciada    Consolas (para IDs, fechas, variables)
```

---

## 4. Medidas actuales

| Superficie | Tamaño | Cuándo |
|---|---|---|
| Orbe en reposo | **108 × 108** | Por defecto |
| Burbuja | **360 × 120** | Respuesta breve |
| Burbuja + entrada de texto | **368 × 168** | Al tocar el orbe |
| Burbuja + pasos o listas | **360 × 176** | Trabajando, o mostrando agenda/memoria |

El orbe empezó en 78 px y se agrandó dos veces (78 → 90 → 108). La geometría está dibujada en un
**espacio de 70 unidades dentro de un Viewbox**, así que cambiar el tamaño es un número.

La burbuja se despliega **hacia el lado que haya espacio** (si el orbe está pegado al borde derecho,
se abre a la izquierda y las esquinas redondeadas se espejan). Vuelve al orbe solo **a los 7 segundos**.

**Principio duro: no hay panel permanente.** Toda expansión es temporal y vuelve sola.

---

## 5. Comportamientos actuales

### Gestos
- **Tocar el orbe (en cualquier parte)** → abre el panel de texto.
- **Mantener y arrastrar (en cualquier parte)** → mueve el orbe. La posición se guarda y se valida
  contra los monitores al reiniciar.
- **Ya NO existe mantener-para-hablar.** Se sacó a propósito: convertía un gesto corriente en una
  toma de micrófono no buscada. Para hablar está el nombre.

### Voz
- Se activa diciendo **«Viernes»** u **«Hola Viernes»** (1 a 8 frases configurables).
- **Sigue escuchando con el orbe oculto** y aparece solo al ser llamado.
- **Aparece sin robar el foco del teclado** — podés estar escribiendo en otra ventana.
- **Mute es el corte duro**: libera el micrófono, apaga wake, cancela la voz.

### La llegada desde la bandeja
Cuando lo llamás estando oculto, la gota **se desprende del ícono del área de notificación**,
**se estira en el trayecto** y **rebota al aterrizar**. 420 ms de viaje + 180 ms de asentamiento.
El estiramiento (squash-and-stretch) es lo que lo hace leer como líquido y no como una ventana que
aparece. Sólo se reproduce si venía realmente oculta.

### Recordatorios
Al vencer: el orbe viene al frente, avisa por globo de bandeja y lo dice en voz alta.
Si la máquina estuvo apagada, los vencidos hace más de 12 h **se marcan en silencio** en vez de
volcarse todos juntos; siguen listados.

### Bandeja
Mostrar/ocultar · Silenciar · Activación por voz · Escuchar aunque esté oculto · Iniciar con Windows · Salir.

---

## 6. Los desplegables (lo que muestra cuando actúa)

Once estados de la burbuja. Los marcados **[nuevo]** son propuestas ya aprobadas pero recién implementadas.

1. **Escribir** — placeholder «Escribí una instrucción…», cursor, botón enviar.
2. **Trabajando [nuevo]** — lista de pasos en pasado / presente / pendiente:
   `Entendí: «…»` → `Leí tu agenda · 3 eventos` → `Escribiendo el cambio…`
   Los bullets codifican estado: verde hecho, periwinkle en curso, **ámbar bloqueado por la política**.
   Esto es clave: hace visible que una respuesta convincente no ejecutó nada por su cuenta.
3. **Agendó algo** — confirmación con título, fecha y dónde quedó guardado.
4. **Tu agenda [nuevo]** — filas estructuradas, hora en monoespaciada + título.
5. **Recordatorio creado** — cuándo va a avisar.
6. **Un recordatorio vence** — ámbar, llega desde la bandeja.
7. **Necesita tu permiso** — franja ámbar con `Confirmar` / `✕`. **Nada pasó todavía.** Vence a los 15 min.
8. **Bloqueado por política** — rojo. Ni con confirmación se ejecuta.
   *El rechazo tiene que verse tan terminado como el éxito: uno que parece error invita a reintentar.*
9. **Qué recuerda de vos** — filas con **ID corto primero** (es lo que hace falta para poder olvidar algo).
10. **Presupuesto agotado** — ámbar, con opción de autorizar sólo por hoy.
11. **Modo local / red caída** — estados sin nube.

---

## 7. Qué es real y qué no (importante para no diseñar humo)

| Capacidad | Estado |
|---|---|
| Recordatorios que suenan | **real** |
| Agenda local | **real**, pero sólo local — no toca Google Calendar |
| Memoria personal | **real** (explícita, revisable, borrable) |
| Conversación | **real** vía OpenRouter |
| Contador de costo | **real**, registra cada llamada |
| Búsqueda web | **simulada** — devuelve la consulta. En camino a ser real |
| Acciones de PC | **simulada** — nunca llama a Windows. En camino a ser real |
| Calendario externo, archivos, navegador | **no existen** |

**Restricción dura del proyecto: sólo se usa la API de OpenRouter.** Ninguna otra.
Eso descarta voz en vivo tipo GPT-Live (no tiene API pública y OpenRouter no tiene canal
bidireccional) y descarta calendario externo (necesitaría OAuth de Google).

---

## 8. Restricciones técnicas — leé esto antes de dibujar

Se implementa en **WPF (.NET 10)**. No es web. Lo que existe en CSS/SVG y **NO** está disponible:

- ❌ Filtros SVG (`feGaussianBlur` + `feColorMatrix`) → **no hay metaballs** sin escribir un shader HLSL
- ❌ CSS, `backdrop-filter`, `mix-blend-mode` arbitrario
- ❌ Webfonts

Lo que **sí** hay y funciona bien:

- ✅ `Path` con `PathGeometry` y **puntos Bézier animados** (es como está hecha la gota)
- ✅ `GeometryGroup` con `FillRule=Nonzero` → **unión de siluetas** (metaballs pobres, sin suavizado)
- ✅ Gradientes lineales y radiales, con **cada stop animable** por separado
- ✅ `DropShadowEffect` (glow), `BlurEffect`
- ✅ Transforms completos + `Storyboard` con easings: Sine, Cubic, Quadratic, **Elastic**, **Back**, Bounce
- ✅ `Viewbox` para escalar todo junto
- ✅ Ventana transparente, sin marco, siempre encima, fuera de la barra de tareas

**Reglas prácticas:**
- Todo tiene que **leerse a 108 px**. Una idea que sólo funciona a 300 px no sirve.
- Tiene que funcionar sobre **escritorio claro y oscuro**.
- Va a estar en pantalla **8 horas seguidas**: movimiento constante y llamativo cansa.
- Multi-monitor y DPI variable.

---

## 9. Decisiones abiertas — acá es donde más ayuda el diseño

1. **¿Qué hace el orbe durante una tarea larga?** Hoy sólo cambia de color y acelera. Una tarea de
   30 segundos se siente igual que una de 2.
2. **La burbuja alta (176 px)** ¿es la respuesta correcta para listas, o hay algo mejor que no rompa
   el principio de «nada permanente»?
3. **El indicador de micrófono.** Había un ícono suelto; se sacó por pedido. Después se probó un aro
   verde y también se sacó. Hoy la única señal es que **la gota entera se pone verde**.
   ¿Alcanza? Es una decisión de privacidad, no sólo estética: **con «escuchar aunque esté oculto»
   activado, el micrófono puede estar abierto sin nada visible en pantalla.**
4. **El override de gasto.** Un botón que gasta plata a un clic es justo lo que el proyecto evita.
   Hoy tiene fricción deliberada (vive en memoria, muere al reiniciar). ¿Cómo se comunica eso?
5. **Multi-monitor**: ¿qué pasa cuando lo llamás y está en otra pantalla?
6. **Estado sin conexión / sin clave** — hoy sólo dice «LOCAL SEGURO».

---

## 10. Qué sería útil que produzcas

- **Refinamiento de la gota**: proporciones, curva de luz, cómo se ve la transición entre estados.
- **Los 11 desplegables**, a escala real (360 × 120 / 368 × 168 / 360 × 176), sobre fondo claro y oscuro.
- **La transición orbe ↔ burbuja**: hoy es un cambio de tamaño de ventana. ¿Cómo debería sentirse?
- **La llegada desde la bandeja**, cuadro por cuadro.
- **Micro-interacciones**: hover, presión, el momento exacto en que reconoce su nombre.
- **Jerarquía tipográfica** dentro de 120 px de alto.
- **Respuestas a las decisiones abiertas** de la sección 9.

**Formato ideal:** especificaciones con medidas, colores en hex, y **duraciones y easings concretos**
—porque se traducen directo a `Storyboard`. «Que se sienta suave» no es implementable; «300 ms
CubicEase EaseOut» sí.

---

## 11. Referencias ya hechas

Existen dos documentos previos con las cinco direcciones exploradas y el sistema completo de la Gota,
incluidas animaciones vivas y los desplegables a escala real. Pedíselos al dueño del proyecto:
`cinco-viernes.html` y `sistema-gota.html`.
