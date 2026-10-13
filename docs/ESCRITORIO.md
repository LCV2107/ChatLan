# Integración con el escritorio de Windows

Código: `src/MiniChat.App/Desktop/DesktopHost.cs` (toda la lógica) y `NativeMethods.cs` (único archivo con
P/Invoke). Ningún otro componente manipula ventanas de Explorer.

## Objetivo

El widget debe sentirse parte del escritorio: las ventanas normales lo cubren; al volver al escritorio
(Win+D o «Mostrar escritorio») sigue visible y se puede usar con teclado y ratón. Prohibido: TopMost
permanente, modificar explorer.exe, inyectar DLL, reemplazar el shell y bucles que fuercen el orden de ventanas.

## Investigación (fase 1, antes de construir el resto)

Se construyó un prototipo y una sonda automática (`tools/MiniChat.DesktopProbe`) que, en el escritorio real,
abre una ventana normal encima del widget, pulsa Win+D, hace clic en el widget, escribe texto Unicode
(«Hola ñandú — ü 😀 你好») con `SendInput`, lo selecciona y copia, y comprueba con `WindowFromPoint` y UI
Automation qué ventana queda visible y dónde está el foco. Antes de escribir comprueba que el foco está
realmente en el widget, para no teclear en otra aplicación, y al terminar restaura las ventanas minimizadas.

Estructura del shell observada en Windows 11 Pro 26200: `Progman` → `SHELLDLL_DefView` (iconos) + `WorkerW`
(fondo), es decir, la disposición de Windows 11 24H2 en adelante.

| Estrategia | Resultado medido |
|---|---|
| Ventana hija de `WorkerW` (detrás de los iconos, estilo «wallpaper») | Descartada sin prototipo: la lista de iconos cubre toda la pantalla y recibe el ratón; un chat necesita entrada fiable. |
| Ventana de herramientas no TopMost + `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` que la sube al mostrarse el escritorio | **Falla Win+D**: Win+D eleva `Progman` por encima de las ventanas normales y `SetWindowPos(HWND_TOP)` devuelve éxito pero no lo supera. |
| **Ventana de herramientas no TopMost con propietario = ventana del shell** (`GWLP_HWNDPARENT` = `GetShellWindow()`) | **7/7**: visible al iniciar; una ventana normal la cubre; tras Win+D sigue visible; fuera del widget se alcanza el escritorio/iconos; el clic le da foco; la escritura Unicode llega completa; selección y copia funcionan; al activar otra ventana vuelve a quedar cubierta. |

Se adoptó la tercera como modo **Widget de escritorio** (predeterminado).

## Qué depende de comportamiento no oficial

1. **Asignar como propietario una ventana de otro proceso** con `SetWindowLongPtr(GWLP_HWNDPARENT)`. Microsoft
   documenta que no debe usarse para cambiar el *padre*; para el *propietario* de una ventana de nivel superior
   funciona, pero no es un contrato. Efecto conocido: las colas de entrada del hilo de UI y de Explorer quedan
   acopladas; si uno se bloquea, el otro puede dejar de responder. Mitigación: la app no hace trabajo
   bloqueante en el hilo de UI (red, disco e imágenes son asíncronos).
2. **Que Win+D eleve `Progman` y respete a sus ventanas con propietario.** Verificado en Windows 11 26200;
   puede cambiar con actualizaciones de Windows. No verificado en Windows 10.
3. **Reconocer el escritorio por la clase `Progman`** de `GetShellWindow()`.

Lo demás es API documentada: `GetShellWindow`, `SetWindowPos`, `RegisterWindowMessage("TaskbarCreated")`,
`MonitorFromRect`, `GetMonitorInfo`.

Si la integración falla (no hay shell de Explorer, o un cambio de Windows rompe el comportamiento), Ajustes
muestra el estado real y ofrece **Ventana compacta**: ventana normal con botón en la barra de tareas, sin
ninguna técnica no documentada, que Win+D minimiza como a cualquier otra. La interfaz lo explica así; no se
presenta como integración.

## Reinicio de Explorer

- Verificado con un proceso propietario simulado: cuando el proceso propietario termina, Windows **no**
  destruye la ventana con propietario de otro proceso; solo deja el propietario a 0.
- Al reiniciarse, Explorer difunde `TaskbarCreated`. `DesktopHost` lo recibe y reasigna el nuevo shell con
  reintentos acotados (0,3 s, 1 s, 3 s, 6 s; no es un bucle permanente). El icono de bandeja se vuelve a
  registrar (WinForms `NotifyIcon` también atiende ese mensaje).
- Verificado en la app final: se quitó el propietario desde fuera (lo que ocurre al morir Explorer) y se envió
  `TaskbarCreated`; la ventana siguió viva y volvió a tener como propietario el shell (262188 → 0 → 262188).
- **No se reinició Explorer de verdad** en la sesión de pruebas (habría cerrado las ventanas del Explorador de
  archivos del usuario). Procedimiento manual en [PRUEBAS.md](PRUEBAS.md#verificación-manual-de-la-integración-con-el-escritorio).
- Los datos nunca dependen de Explorer: un reinicio no afecta a conversaciones ni a la red.

## Monitores, resolución y DPI

- Manifiesto `PerMonitorV2`: WPF reescala al mover la ventana entre monitores con distinto DPI.
- Posición y tamaño se guardan (2 s después del último cambio y al salir). Al arrancar, al cambiar la
  configuración de pantallas (`SystemEvents.DisplaySettingsChanged`), al expandir y al mostrarla desde la
  bandeja, `WindowPlacement.EnsureVisible` comprueba en píxeles físicos que la barra superior está dentro del
  área de trabajo de algún monitor; si no, la mueve al monitor más cercano y la reduce si no cabe.
- Verificado: con una posición guardada en (6000, 2500), fuera de los dos monitores de 1920×1080, la ventana
  apareció en (3460, 492), dentro del monitor más cercano.
- **No verificado**: desconexión física de un monitor y cambio del escalado de Windows (habría alterado la
  configuración de pantalla del usuario). Procedimiento manual en PRUEBAS.md.

## Otros comportamientos

- No TopMost, sin botón en la barra de tareas en modo widget, no aparece en Alt+Tab (ventana de herramientas):
  se accede desde el icono de bandeja.
- Mostrar desde la bandeja sí activa la ventana (lo pidió el usuario); los mensajes y solicitudes nunca la activan.
- Contraer deja solo la barra de 36 px; ocultar la lleva a la bandeja sin cerrar la red; **Salir** está en el
  menú de la bandeja.
