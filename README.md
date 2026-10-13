# MiniChat LAN

Widget de escritorio para Windows que permite conversar (texto y stickers) con compañeros conectados a la
**misma red local**, sin Internet, sin cuentas, sin números de teléfono y sin servidor central. Cada equipo
ejecuta la misma aplicación y se comunica directamente con los demás.

Recorrido principal: **detectar al compañero → solicitar chat → aceptar → intercambiar textos, stickers y documentos →
cerrar y recuperar la conversación.**

| | |
|---|---|
| Plataforma | Windows 10 y Windows 11, x64 (verificado solo en Windows 11 Pro 10.0.26200; ver [limitaciones](#limitaciones-y-problemas-pendientes)) |
| Tecnología | C# · .NET 10 (LTS, soporte hasta noviembre de 2028) · WPF nativo (sin navegador embebido) |
| Datos | `%LocalAppData%\MiniChatLAN\` (por usuario de Windows; nunca en Program Files) |
| Red | UDP multicast 239.255.77.77:**47820** (descubrimiento) · TCP **47821** con TLS mutuo (conversaciones) |

Documentación detallada:

- [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md) — componentes, decisiones, seguridad, persistencia, stickers.
- [docs/PROTOCOLO.md](docs/PROTOCOLO.md) — protocolo versionado: tramas, operaciones, límites, errores.
- [docs/ESCRITORIO.md](docs/ESCRITORIO.md) — integración con el escritorio y lo que depende de comportamiento no oficial.
- [docs/PRUEBAS.md](docs/PRUEBAS.md) — resultados de pruebas, mediciones y **procedimiento de prueba entre dos PCs**.
- [docs/DEPENDENCIAS.md](docs/DEPENDENCIAS.md) — versiones, licencias e impacto en tamaño.

## Estructura

```
MiniChatLAN.slnx
src/
  MiniChat.Core/        Motor sin interfaz: identidad, red, descubrimiento, protocolo, transporte TLS,
                        solicitudes/autorización, mensajería, persistencia SQLite, stickers, actualizaciones, registro.
  MiniChat.App/         Aplicación WPF (MiniChat.exe): vistas, ViewModels y Desktop/ (todo el Win32 aislado).
tests/MiniChat.Tests/   153 pruebas automatizadas (xUnit v3), incluidas pruebas entre procesos.
tools/
  MiniChat.Peer/        Par sin interfaz con el mismo motor: pruebas multiproceso y diagnóstico en red real.
  MiniChat.DesktopProbe/ Sonda que verifica la integración con el escritorio (Win+D, foco, escritura…).
  IconGen/              Generador de los iconos (uso puntual; fuera de la solución).
  firewall-dev.ps1      Reglas de firewall limitadas para probar compilaciones de desarrollo (opcional).
  prueba-sin-internet.ps1  Corta Internet SOLO a MiniChat para demostrar que funciona con la red local.
  firewall-subredes.ps1    (Administrador) Permite otras subredes de la oficina en el firewall y como directiva del equipo.
  firmar-release.ps1       (PowerShell 7) Firma una release para el actualizador: genera updates\<canal>.json(.sig).
updates/                Manifiestos firmados que el actualizador descarga de GitHub (stable/experimental).
installer/MiniChat.Installer/  Proyecto WiX 6 del instalador MSI (fuera de la solución a propósito).
licenses/               Licencias y avisos de terceros (se distribuyen con la aplicación).
```

## Requisitos para compilar

- Windows 10/11 x64.
- .NET SDK 10.0.100 o posterior de la banda 10.0 (`global.json`; probado con 10.0.401).
- Acceso a nuget.org la primera vez (restauración de paquetes). La aplicación **no** usa Internet al ejecutarse.

## Compilar, probar y ejecutar

Desde la raíz del repositorio:

```powershell
dotnet build MiniChatLAN.slnx -c Release
dotnet test --project tests\MiniChat.Tests          # 153 pruebas (~1 min)
src\MiniChat.App\bin\Release\net10.0-windows\win-x64\MiniChat.exe
```

Argumentos de `MiniChat.exe`:

| Argumento | Uso |
|---|---|
| `--profile <nombre>` | Perfil de datos independiente en `%LocalAppData%\MiniChatLAN\profiles\<nombre>\`. Un mismo perfil no puede abrirse dos veces; perfiles distintos sí. |
| `--minimized` | Arranca oculto en la bandeja (lo usa el inicio con Windows). |
| `--desktop-mode DesktopWidget\|CompactWindow` | Fuerza el modo de ventana en esta ejecución. |
| `--tcp-port N`, `--discovery-port N`, `--data-dir <ruta>` | **Solo desarrollo/pruebas** (p. ej. dos perfiles en la misma PC). Las reglas de firewall del MSI cubren únicamente los puertos por defecto. |

### Probar dos instancias en una misma PC

La red debe estar autorizada en ambos perfiles (Ajustes › Redes). Ejemplo:

```powershell
$exe = "src\MiniChat.App\bin\Release\net10.0-windows\win-x64\MiniChat.exe"
& $exe --profile ana --tcp-port 47821
& $exe --profile bea --tcp-port 47831
```

Al ejecutar binarios **sin instalar** que escuchan en la red, Windows puede mostrar el aviso
«¿Permitir que MiniChat se comunique…?» y, mientras no se responda, crea reglas de bloqueo de entrada para
ese ejecutable. El tráfico dentro de la misma PC no se ve afectado. Para pruebas entre PCs sin instalar,
ver `tools\firewall-dev.ps1` o usar el MSI.

## Configuración de red

Ajustes › Redes › **Diagnosticar red** detecta y explica cómo resolver los problemas más comunes: red Pública
o «no identificada», IP fija sin puerta de enlace, IP duplicada, adaptador sin IP (169.254), ninguna red
autorizada, puertos ocupados, reglas del firewall de Windows que bloquean MiniChat, máscaras de subred distintas
entre equipos y ningún compañero visible (multicast bloqueado o aislamiento Wi-Fi). En la conversación,
⋯ › **Probar conexión** comprueba identidad, cifrado (TLS) y tiempo de respuesta con ese contacto.
Ajustes › **Prueba sin Internet** y `tools\prueba-sin-internet.ps1` demuestran que funciona solo con la red local.

- **Descubrimiento**: anuncios UDP multicast a `239.255.77.77:47820` (ámbito administrativo local, TTL 1:
  no salen de la subred) cada 10 s ± 2 s; un equipo deja de figurar como disponible a los 35 s sin anuncios.
  Solo aparecen equipos que ejecutan MiniChat: no se escanean direcciones ni puertos.
- **Conversaciones**: TCP 47821 con TLS 1.2/1.3 y autenticación mutua por certificado.
- **Redes autorizadas** (Ajustes › Redes): por defecto solo interfaces con perfil de Windows **Privado o
  Dominio** que no parezcan VPN/virtuales; se excluyen loopback, túneles, PPP y APIPA (169.254/16). Las redes
  **Públicas**, VPN o virtuales se usan solo si el usuario las autoriza explícitamente. Una IP privada no
  basta: la dirección remota debe pertenecer a la subred de una interfaz autorizada, tanto al conectar como
  al recibir. Se escucha solo en las IP de las interfaces autorizadas (no en 0.0.0.0).
- **Firewall**: el MSI crea 4 reglas de entrada limitadas a `MiniChat.exe`, TCP 47821 y UDP 47820, perfiles
  **Privado y Dominio**, ámbito **subred local**. No desactiva el firewall ni abre la red Pública. Si Windows
  clasifica tu red de trabajo como Pública, la opción recomendada es cambiarla a Privada en
  Configuración › Red e Internet (requiere permisos del equipo); autorizarla solo dentro de MiniChat no basta
  para recibir conexiones porque el firewall de Windows las seguirá bloqueando.
- Sin servicios externos, retransmisores, túneles, UPnP ni apertura de puertos del router.
- Por defecto solo se usa la subred propia. Otras subredes de la oficina, solo si se añaden (ver abajo).
  No hay conectividad con equipos aislados por firewall ni con redes Wi-Fi con aislamiento de clientes.

### Otras subredes de la oficina

Cuando cada área o piso tiene su propia subred (por ejemplo `10.10.160.x`, `10.10.161.x`…) unida por el
router, el multicast no llega de una a otra. Ajustes › **Otras subredes de la oficina** permite:

- **Añadir subredes** (`10.10.161.0/24`). Solo rangos privados (10/8, 172.16/12, 192.168/16) de /16 a /30.
  MiniChat las acepta al conectar y al recibir, y sale por la interfaz autorizada que indique la tabla de
  rutas de Windows. Lo que no esté en la lista se sigue rechazando.
- **Buscar compañeros** en ellas: un sondeo UDP sin nombres a cada dirección (20 por segundo; una /24 tarda
  unos 13 s; se repite cada 15 min; primero las últimas IP conocidas de los contactos). Solo responden los
  equipos con MiniChat que **también** hayan autorizado la subred de usted; después se mantienen por unicast.
  Se puede desactivar y seguir usando «Conectar por IP».
- **Explorar subredes**: ping a la puerta de enlace habitual (primera y última dirección) de cada bloque del
  mismo tamaño que la red propia dentro de su /16. Solo al pulsarlo (unos 500 pings).
- **Probar una IP**: red a la que pertenece, interfaz de salida, ping, routers intermedios (hasta 8) y puerto
  TCP 47821, con un veredicto (se puede / MiniChat no está abierto / algo lo bloquea / las redes no están unidas).

El firewall de cada equipo debe admitir esas subredes: las reglas del MSI solo admiten la subred local.
Como administrador, en cada equipo:

```powershell
.\tools\firewall-subredes.ps1 -Subredes 10.10.161.0/24,10.10.162.0/24   # reglas + directiva para todos los usuarios
.\tools\firewall-subredes.ps1 -Estado                                    # ver lo configurado (no requiere administrador)
.\tools\firewall-subredes.ps1 -Quitar
```

La directiva se guarda en `HKLM\SOFTWARE\MiniChatLAN\SubredesRemotas`; esas subredes aparecen como «definidas
por el administrador» y no se pueden quitar desde la aplicación. Desinstalar el MSI no borra ni estas reglas
ni la directiva: use `-Quitar`. Diagnosticar red avisa si el firewall no cubre alguna subred de la lista, si no
hay ruta hacia ella o si nadie respondió. Si las redes no están unidas por un router (dos redes separadas o una
Wi-Fi de invitados), ningún programa puede comunicarlas: eso lo resuelve TI.

## Ubicación de los datos

`%LocalAppData%\MiniChatLAN\` (perfil por defecto) o `%LocalAppData%\MiniChatLAN\profiles\<nombre>\`:

| Ruta | Contenido |
|---|---|
| `minichat.db` (+ `-wal`, `-shm`) | SQLite: perfil, identidad (nombre de la clave), ajustes, contactos, relaciones, solicitudes, conversaciones, mensajes, referencias a stickers |
| `stickers\ab\<sha256>.mcs` | Imágenes de stickers **cifradas** (una sola copia por contenido) |
| `files\<id>.mcf` (`.part` mientras se descarga) | Documentos adjuntos **cifrados** |
| `abiertos\` | Copias temporales sin cifrar de documentos abiertos (se borran al bloquear y al arrancar) |
| `logs\minichat.log` (+ hasta 4 rotados de 1 MB) | Diagnóstico, sin textos de mensajes ni claves |
| `backups\` | Copia automática de la base de datos antes de cada migración de esquema |

La **clave privada** de identidad no está en esa carpeta: vive en el almacén de claves CNG del usuario de
Windows (no exportable, protegida por DPAPI). Desinstalar o actualizar no borra estos datos.

## Documentos adjuntos

Botón 📎 en la conversación. Hasta **100 MB** por archivo. El receptor ve nombre, tipo y tamaño y debe
**aceptar** cada archivo antes de que se descargue. La transferencia va por la red local (no necesita
Internet), cifrada con TLS, se reanuda sola tras un corte y se verifica con SHA-256 al terminar. En ambos
equipos se guarda cifrada. «Guardar como…» y «Abrir» crean copias sin cifrar marcadas por Windows como
recibidas de otro equipo. Los ejecutables, scripts e instaladores se permiten con advertencia y nunca se
abren solos. Detalle en [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md#documentos-adjuntos).

## Seguridad del historial

- **Cifrado en reposo**: textos de mensajes, borradores, nombres de contactos y de sus PCs, nombres de stickers
  y las imágenes de los stickers se guardan cifrados (ECIES: ECDH P-256 + HKDF-SHA256 + AES-256-GCM, con
  primitivas estándar de .NET/Windows). Leerlos requiere **el PIN y la cuenta de Windows** del usuario a la vez
  (PBKDF2-SHA256 con 600 000 iteraciones + un secreto protegido con DPAPI).
- **PIN obligatorio** (mínimo 6 caracteres, sin secuencias). Bloqueo configurable: tras N minutos sin uso
  (1–60, por defecto 15) o mantener abierto mientras la app esté abierta; opcionalmente al bloquear Windows;
  siempre al reiniciar la app; y manual con el candado del encabezado. Intentos fallidos con esperas crecientes.
- **Bloqueado**, la app sigue recibiendo y guardando mensajes (cifrados con la clave pública), pero no se pueden
  leer ni enviar; la pantalla y la memoria se vacían. Los avisos no muestran remitente ni texto.
- **Carpeta de datos** accesible solo para el usuario y SYSTEM. SQLite con `secure_delete` (lo borrado se
  sobrescribe). Los datos de versiones anteriores se cifran al crear el PIN y se purgan sus restos del archivo.
- Opción de **ocultar la ventana en capturas y grabaciones** de pantalla (desactivada por defecto).
- **PIN olvidado**: el historial es irrecuperable; se puede crear un PIN nuevo borrando el historial local
  (contactos y autorizaciones se conservan).
- **Límites**: quedan sin cifrar los metadatos (quién, cuándo, cuántos, IP), el nombre visible propio y los
  hashes de los stickers; no protege frente a programas espía que vean la pantalla o el teclado mientras está
  desbloqueado, ni frente a quien conozca el PIN y controle la cuenta de Windows. Las exportaciones a texto no
  se cifran (se avisa). Detalle en [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md#cifrado-del-historial).

## Instalador MSI

El proyecto `installer\MiniChat.Installer` publica la aplicación autocontenida (no requiere instalar .NET en
el equipo destino) y genera el MSI. Instala por equipo en `Program Files\MiniChat LAN`, crea un acceso en el
menú Inicio y las reglas de firewall descritas arriba. El comando está al final de
[docs/PRUEBAS.md](docs/PRUEBAS.md#generar-el-instalador-msi).

- **Una sola instalación**: el `UpgradeCode` fijo + `MajorUpgrade` (con `AllowSameVersionUpgrades`) hacen que
  instalar un MSI reemplace cualquier versión anterior, igual o distinta. Nunca quedan dos MiniChat.
- **Versión**: fíjala en `Directory.Build.props` (`<Version>`) antes de cada release; ese único valor alimenta
  la versión interna, la del MSI y el nombre del archivo. Los datos de `%LocalAppData%` se conservan al actualizar.

## Actualizaciones

Opcionales y manuales, desde GitHub (repo público). MiniChat **no necesita Internet**; si lo hay, en Ajustes ›
Actualizaciones puedes **Buscar actualizaciones** e instalar con un botón. Dos canales: **Estable** (releases
normales) y **Experimental** (pre-releases, para probar antes de desplegar). Un aviso al arrancar (desactivable)
indica si hay versión nueva; nunca obliga a estar conectado ni bloquea el uso.

Seguridad: se descarga un **manifiesto firmado** (ECDSA P-256) y se verifica con la clave pública incrustada
antes de nada; el MSI se comprueba por **SHA-256** antes de ejecutarse. Así, ni comprometiendo GitHub se puede
empujar una actualización sin la clave privada de firma (que nunca está en GitHub ni en la app). No hay
instalación silenciosa ni servicios con privilegios: el usuario pulsa, acepta el UAC y se instala.

Activarlo (una vez): ejecuta `pwsh tools\firmar-release.ps1 -Init`, pega la clave pública en
`UpdateConfig.TrustedPublicKeyBase64` y pon tu repositorio en `UpdateConfig.Repository`
([src/MiniChat.Core/Update](src/MiniChat.Core/Update/UpdateTypes.cs)). Procedimiento completo de publicación en
[docs/ACTUALIZACIONES.md](docs/ACTUALIZACIONES.md).

## Limitaciones y problemas pendientes

- **Actualizaciones**: requieren Internet en el equipo al comprobar/descargar (es opcional; sin red no pasa
  nada). El canal experimental lo activa cada usuario. El aviso del UAC mostrará «editor desconocido» salvo que
  firmes el MSI con Authenticode (opcional; una CA interna del dominio lo evita gratis). No probado aún contra un
  GitHub real: verificado con firma real y HTTP simulado, y el roundtrip de firma PowerShell↔.NET.
- **Otras subredes**: ambas partes deben añadir la subred de la otra y permitirla en su firewall. La búsqueda
  automática envía tráfico a todas las direcciones de esas subredes y el área de TI puede verlo como un
  barrido. «Explorar subredes» supone que las subredes vecinas tienen el mismo tamaño que la propia y que su
  router responde a ping. No se ha probado todavía entre subredes reales: solo con red simulada y loopback.

Resumen (detalle en los documentos enlazados):

1. **No se ha probado entre dos PCs físicas** (solo se disponía de una). Todo lo de red se verificó entre
   procesos en una sola PC, por loopback y por la interfaz Ethernet real. Procedimiento para validarlo:
   [docs/PRUEBAS.md](docs/PRUEBAS.md#procedimiento-de-prueba-entre-dos-pcs).
2. **Integración con el escritorio**: depende de comportamiento no documentado de Explorer (aislado en
   `Desktop/DesktopHost.cs`, con modo «Ventana compacta» como alternativa). Verificado en Windows 11 26200;
   no verificado en Windows 10. El reinicio real de Explorer y los cambios de escalado DPI no se ejecutaron
   (sí la recuperación simulada y la recolocación fuera de pantalla). Ver [docs/ESCRITORIO.md](docs/ESCRITORIO.md).
3. La sonda de entrada (Win+D, foco, escritura) contra la aplicación final quedó bloqueada por avisos del
   firewall pendientes en la sesión de pruebas; la misma técnica superó todas las comprobaciones con el
   prototipo de la fase 1. Repetir con `tools\MiniChat.DesktopProbe` (ver docs/PRUEBAS.md).
4. El historial está cifrado, pero no sus metadatos; si se olvida el PIN, el historial no se puede recuperar.
5. El emparejamiento es de tipo «confiar en el primer uso» con código comparable; si los usuarios no comparan
   el código, un atacante activo en la LAN podría interponerse en el primer emparejamiento.
6. WPF dibuja los emoji en monocromo.
7. Solo IPv4. El descubrimiento requiere que la red permita multicast local; si no, usar «Conectar por IP».
