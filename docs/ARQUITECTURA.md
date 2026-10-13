# Arquitectura y decisiones

## Visión general

```
┌──────────────────────────── MiniChat.App (WPF, MiniChat.exe) ─────────────────────────────┐
│ Views (XAML)  ←→  ViewModels (MVVM propio)  ←→  ChatEngine (eventos → hilo de UI)         │
│ Desktop/: DesktopHost (integración con el escritorio), NativeMethods (único P/Invoke),     │
│           TrayIcon, WindowPlacement, StartupRegistration, SingleInstance                   │
└────────────────────────────────────────────┬──────────────────────────────────────────────┘
                                             │
┌──────────────────────────── MiniChat.Core (sin interfaz) ─────────────────────────────────┐
│ Engine/      ChatEngine: solicitudes, autorización, mensajería, colas, exportación          │
│ Discovery/   DiscoveryService: UDP multicast, presencia, límites de tasa                    │
│ Transport/   TransportServer (entrante), PeerClient (saliente), TlsPolicy, ConnectionPool   │
│ Protocol/    FrameCodec, paquetes versionados, PacketValidator                              │
│ Identity/    LocalIdentity (clave CNG), códigos de identidad y emparejamiento               │
│ Network/     NetworkPolicy (interfaces/subredes autorizadas), WindowsNetworkInventory (NLM) │
│ Persistence/ Database (SQLite), Migrations, repositorios                                    │
│ Stickers/    StickerProcessor (SkiaSharp), StickerService (biblioteca y almacén por hash)   │
│ Config/, Diagnostics/, Model/                                                               │
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

- `MiniChat.Core` no referencia WPF: toda la comunicación y el almacenamiento se prueban sin abrir la
  interfaz (59 pruebas, incluidas pruebas entre procesos con `tools/MiniChat.Peer`).
- La base de datos no conoce el formato de red: los paquetes (`Protocol/Packets.cs`) se traducen a modelos
  (`Model/Models.cs`) en el motor.
- Las llamadas Win32 están solo en `MiniChat.App/Desktop/NativeMethods.cs`. La única interoperabilidad
  adicional es COM con el Network List Manager (API documentada) en `WindowsNetworkInventory`.
- Tamaño proporcionado: sin contenedor de inyección de dependencias, sin ORM, sin framework MVVM externo.

## Decisiones principales

| Decisión | Motivo | Alternativas descartadas |
|---|---|---|
| **.NET 10 (LTS)** + **WPF** | Soporte hasta nov-2028; interfaz nativa con buena integración Win32 y DPI por monitor | WinUI 3 (empaquetado y ventana no adecuados para el widget), navegador embebido (consumo) |
| WinForms solo para `NotifyIcon` | Icono de bandeja oficial incluido en el runtime de escritorio | Implementar `Shell_NotifyIcon` a mano (más código Win32) |
| **SQLite** (Microsoft.Data.Sqlite) | Transaccional, un archivo por usuario, tolerante a cierres; sin servidor | Archivos JSON (sin atomicidad), LiteDB, EF Core (tamaño) |
| **SkiaSharp** para stickers | Decodificador WebP incluido: no depende de códecs de Windows (WIC/WPF solo decodifica WebP si está instalado el códec «Extensiones de imagen WebP», cuya presencia no está garantizada en todos los equipos), MIT | WIC/WPF (WebP no garantizado), ImageSharp (licencia Six Labors Split) |
| Identidad = clave **ECDSA P-256 en CNG** | Clave no exportable protegida por Windows (DPAPI del usuario); huella SHA-256 como identificador | Guardar PFX en disco, usar nombre/PC/IP |
| **TLS mutuo con fijación de clave** | Cifrado estándar (SChannel) sin CA ni servidor; identidad demostrada por posesión de la clave | Cifrado propio (prohibido), TLS sin certificado de cliente |
| **UDP multicast** con TTL 1 | Solo responden instancias de MiniChat; no sale de la subred | Escaneo de rangos/puertos sin autorización, mDNS/DNS-SD (dependencias y servicio Bonjour) |
| Otras subredes: **lista explícita + sondeo unicast** | El multicast no cruza routers. Solo subredes privadas añadidas por el usuario o el administrador; el sondeo no lleva nombres y solo responde quien también autorizó la subred | Servidor o retransmisor central (prohibido), intercambio de listas entre contactos (revela quién usa MiniChat), multicast enrutado (requiere configurar los routers) |
| Transporte petición/respuesta | Cada lado envía por su conexión saliente; el servidor solo responde: sin concurrencia de tramas | Conexión bidireccional persistente (más estados de error) |
| **Reintento manual** | Requisito de la primera versión; el usuario decide y ve el estado | Reintentos automáticos (reconexiones agresivas) |
| Instalador **WiX 6** (MSI) | Formato estándar, reglas de firewall declarativas | MSIX (sandbox, firewall y arranque más limitados) |

## Identidad y confianza

- Primera ejecución: se crea la clave `MiniChatLAN-<guid>` en el *Microsoft Software Key Storage Provider*
  del usuario (`ExportPolicy = None`). En la base de datos solo se guarda el **nombre** de la clave y su huella.
- **Huella** = SHA-256 de `SubjectPublicKeyInfo` (64 hex). Es el identificador del contacto. El nombre
  visible, el nombre de PC y la IP son datos informativos: dos PCs con el mismo nombre se distinguen; un cambio
  de IP actualiza el contacto existente.
- **Código de identidad** (Ajustes): primeros 80 bits de la huella en 5 grupos hex.
- **Código de emparejamiento**: SHA-256 de `"MiniChatLAN pairing v1" + huellaMenor + huellaMayor`, 24 dígitos
  (~79 bits) en 6 grupos. Es simétrico (ambos ven el mismo) y depende de las dos claves; se muestra al recibir
  la solicitud, en la solicitud enviada y en el menú de la conversación, para compararlo por otro medio.
- Si la clave se pierde (reinstalación), se crea otra y la app avisa (`IdentityRegenerated`). Sus contactos
  la verán como un equipo nuevo, marcado como «mismo equipo que X, pero con otra identidad»; al intentar
  escribir al contacto antiguo en su dirección se obtiene `IdentityMismatchException` y un mensaje claro.
  Hace falta un emparejamiento nuevo.

### TLS

- `SslStream` (SChannel). Protocolos permitidos: TLS 1.2 y 1.3 (TLS 1.3 solo donde Windows lo admite).
- Certificado autofirmado regenerado en cada arranque con la clave persistente (la identidad depende de la
  clave, no del certificado). Cadena con `DisableCertificateDownloads` y sin revocación: nunca se accede a
  Internet.
- Validación (`TlsPolicy.AcceptPeerCertificate`): se exige certificado del par y clave ECDSA P-256; se aceptan
  los errores de cadena inherentes a un certificado autofirmado. Esto **no** es desactivar la validación: la
  confianza se traslada a la huella, que (1) se compara con la esperada al conectar con un contacto conocido y
  (2) debe estar autorizada en la base de datos del receptor para cualquier operación de mensajería.

## Política de red

`NetworkPolicy` decide qué interfaces se usan (ver README). Además:

- El servidor TCP escucha en cada IP autorizada por separado y vuelve a comprobar cada conexión entrante
  (`IsAllowedInbound`: IP local autorizada y remota dentro de su subred).
- El cliente se enlaza a la IP de la interfaz cuya subred contiene el destino.
- Los sockets de descubrimiento se abren por interfaz autorizada; los datagramas de fuera de su subred se ignoran,
  salvo los de una subred de otra área autorizada.
- **Otras subredes** (`Network/SubnetRange.cs`, `Engine/ChatEngine.Subnets.cs`): lista de usuario
  (`net.remoteSubnets`) + directiva del administrador (`HKLM\SOFTWARE\MiniChatLAN\SubredesRemotas`). Para un
  destino de esa lista, `InterfaceFor` pregunta a Windows qué dirección local usaría (socket UDP «conectado»,
  que no envía nada) y solo la acepta si es de una interfaz autorizada. Una subred de la lista nunca habilita
  direcciones de una red conectada que esté denegada (p. ej. una Pública).
- Descubrimiento en otras subredes: `qry` unicast con `u:1` y sin nombres a cada dirección (20/s, primero las
  últimas IP de los contactos); quien lo recibe y lo autoriza responde con su `ann` por unicast (como mucho
  una vez cada 5 s por origen). Los equipos encontrados así reciben los `ann`/`bye` periódicos por unicast.
- Cambios de red (`NetworkChange`) se agrupan 2 s y reconfiguran escucha y descubrimiento.

## Estados de relación y solicitudes

Disponibilidad (presencia en red) y relación (autorización) son independientes. Estados (`Model/Relation`):

| Estado | Significado | Acciones |
|---|---|---|
| (descubierto) | Se anuncia en la red; aún no es contacto | Solicitar chat |
| `OutgoingPending` | El receptor confirmó haber recibido mi solicitud | Cancelar |
| `IncomingPending` | Me pidió chatear | Aceptar · Rechazar · Bloquear |
| `Authorized` | Consentimiento de ambos | Escribir · Bloquear |
| `RejectedByMe` / `RejectedByPeer` | Solicitud rechazada (no autoriza) | Nueva solicitud (con espera) |
| `RevokedByPeer` | Al enviarle un mensaje respondió que ya no me autoriza | Nueva solicitud |
| `None` | Sin autorización (tras cancelar, desbloquear, expirar…) | Nueva solicitud |
| *bloqueado* (bandera aparte) | Rechaza solicitudes y mensajes; conserva historial | Desbloquear (queda en `None`) |

Casos límite resueltos (todos con prueba automatizada):

- **Solicitudes simultáneas**: si me llega su solicitud mientras la mía está pendiente, o recibo «pendiente»
  estando ya en «entrante pendiente», ambos consintieron → `Authorized` en los dos lados.
- **Duplicadas**: se reutiliza el identificador; el receptor responde «pendiente» sin volver a avisar.
- **Repetición**: tras rechazar, 10 min sin avisos; máximo 3 solicitudes nuevas por par y hora y 30 en total.
- **Aceptación perdida**: la decisión se guarda primero; si no se entrega, se reintenta cuando el par vuelve a
  anunciarse (mínimo 30 s entre intentos) o cuando reenvía su solicitud («ya autorizado»). Un mensaje de un par
  al que habíamos solicitado chat también confirma la aceptación.
- **Cancelación antes de la respuesta**: el receptor pasa a `None`; si aceptó sin enterarse, el solicitante
  responde «cancelada» y la autorización se deshace.
- **Cierre con solicitudes pendientes**: se conservan; expiran a los 7 días.
- Cada transición se decide en **una transacción** SQLite: no quedan estados a medias.

## Mensajería

- El mensaje saliente se guarda (`Pending`) antes de transmitirse; la caja de texto solo se vacía si se guardó.
- Cola por conversación en orden de creación. Si un envío falla, los posteriores esperan (`Pending`) y el
  reintento manual reenvía todos en orden. El receptor nunca ve un orden distinto al del emisor.
- Estados visibles: Pendiente · Enviando · **Entregado** (el receptor respondió tras **guardar** el mensaje con
  `synchronous=FULL`; no implica lectura) · **No confirmado** (se transmitió sin respuesta) · **Fallido**.
- Identificador estable (`uid`): reintentar nunca duplica (restricción única en el receptor → «duplicate»,
  que cuenta como entregado).
- Al arrancar, «Enviando» pasa a «No confirmado» (la app se cerró tras transmitir y antes de la confirmación)
  y lo pendiente a «Fallido»; nada se reenvía sin que el usuario lo pida.
- Reintentar respeta bloqueos y revocaciones. Si el receptor limita la tasa, el emisor espera y reintenta (no
  es un fallo de entrega).
- Orden estable sin depender de relojes: el orden visible es el identificador local autoincremental; la hora
  del emisor es solo informativa.

## Persistencia

- SQLite en WAL con `synchronous=FULL`, `foreign_keys=ON`; escrituras serializadas y siempre en transacción.
- Tablas separadas: `profile`, `identity`, `settings`, `contacts`, `relationships`, `requests`, `request_log`,
  `conversations`, `messages`, `sticker_blobs`, `sticker_library` (ver `Persistence/Migrations.cs`).
- **Migraciones** numeradas con `PRAGMA user_version`. Antes de migrar una base existente se hace
  `VACUUM INTO backups\minichat-v<N>-<fecha>.db`. Una base de una versión futura se rechaza sin tocarla.
- **Corrupción**: `PRAGMA quick_check` al abrir; si falla, o el archivo no es SQLite, la app informa, ofrece
  abrir la carpeta y **no modifica ni borra nada**.
- El historial se carga en páginas de 50 mensajes; las conversaciones no se mantienen completas en memoria.
- Exportación a texto UTF-8 (**sin cifrar**; la interfaz lo advierte). Los stickers se escriben como `[Sticker: <12 hex>.png (ancho×alto)]` y sus PNG se
  copian a `<nombre>_stickers\`. Borrar el historial es una acción explícita, con confirmación que aclara que
  la copia del compañero no se borra.

## Stickers

- Importación de PNG, JPG y WebP **verificando la firma binaria** (no la extensión) y que el decodificador
  coincide. Límites: 5 MB por archivo, 4096×4096 y 16,7 Mpx (comprobados antes de reservar memoria: máximo
  64 MB por decodificación), 2 decodificaciones simultáneas.
- Normalización a **PNG** de hasta 512 px de lado, conservando proporciones y transparencia, sin metadatos.
  Orientación EXIF aplicada. Nada se ejecuta: solo se leen bytes.
- **Animados**: se conserva solo el primer fotograma y se informa en el resultado de la importación.
- Almacén por contenido `stickers\ab\<sha256>.mcs` (PNG **cifrado**): sin duplicados. El mensaje viaja con el PNG en el cuerpo
  de la trama (≤ 1 MB): el receptor no necesita tenerlo antes ni depende de rutas o enlaces.
- El receptor valida tamaño, firma PNG, hash, dimensiones y decodificación completa antes de guardar.
- Quitar de la biblioteca no borra el PNG si algún mensaje lo usa; la limpieza solo elimina imágenes sin
  referencias.
- Biblioteca: importar varios, miniaturas (decodificadas fuera del hilo de UI, caché acotada a 96), favoritos
  y recientes.

## Documentos adjuntos

Código: `Engine/ChatEngine.Files.cs`, `Files/EncryptedFileStore.cs`, `Files/FileNames.cs`.

- Hasta **100 MB** por archivo (`MiniChatOptions.MaxFileBytes`), trozos de 1 MB, 2 descargas simultáneas.
- **Emisor**: copia el archivo a su almacén cifrado (así el original puede cambiar o borrarse) y pone una
  oferta en la cola de mensajes de la conversación (mismo orden, estados y reintento manual que el texto).
- **Receptor**: guarda la oferta (incluso bloqueado). **Nada se descarga hasta que el usuario pulsa Aceptar.**
  Antes comprueba que hay espacio en disco. Pide los trozos, los guarda cifrados y al final verifica el
  SHA-256 de todo el contenido; si no coincide, lo descarta y lo marca como fallido.
- **Reanudación**: el progreso se guarda trozo a trozo. Un corte, un cierre o un bloqueo de cualquiera de los
  dos pausa la transferencia; continúa sola al desbloquear o cuando el compañero vuelve a anunciarse.
- **Cifrado**: en tránsito, TLS. En disco, cada documento usa su propia clave AES-256-GCM (trozos con nonce y
  dato asociado `fid ‖ índice`, que impiden reordenar o mezclar trozos de archivos distintos); esa clave se
  guarda cifrada por la bóveda (PIN + Windows). Los nombres de archivo también se guardan cifrados.
- **Abrir / Guardar como**: descifran el documento verificando de nuevo su huella. «Abrir» lo hace en la carpeta
  privada `abiertos\` (accesible solo para el usuario); esa copia sin cifrar se borra al bloquear la app y al
  arrancarla (si el programa que la usa la tiene abierta, se borra la siguiente vez). Toda copia descifrada
  recibe la marca «descargado de otro equipo» (`Zone.Identifier`, ZoneId=3), así que Windows/SmartScreen y
  Office la tratan con las mismas precauciones que una descarga de Internet.
- **Tipos peligrosos** (ejecutables, scripts, instaladores, accesos directos, documentos con macros…): se
  permiten, se señalan con una advertencia en la burbuja y requieren confirmación explícita para abrirlos.
  MiniChat nunca abre ni ejecuta nada por sí mismo.
- **Nombres**: se eliminan rutas, caracteres no válidos, nombres reservados de Windows y los caracteres de
  control bidireccional usados para disfrazar extensiones (`factura‮fdp.exe` → `facturafdp.exe`).
- **Compatibilidad**: requiere protocolo v2. Con un compañero en v1 los mensajes funcionan y el envío del
  documento falla con un mensaje claro.
- Borrar el historial elimina también los documentos de esa conversación en este equipo; exportar los
  representa como `[Archivo: nombre (tamaño)]`.

## Cifrado del historial

Código: `MiniChat.Core/Security/Vault.cs` y `Engine/ChatEngine.Security.cs`. Solo primitivas estándar de
.NET/Windows (CNG y DPAPI); no hay criptografía propia.

| Pieza | Detalle |
|---|---|
| Par de claves de almacenamiento | ECDH P-256. La **pública** está en claro en la tabla `vault`; la **privada** cifrada con AES-256-GCM (dato asociado: la clave pública) |
| Clave que protege la privada (KEK) | `HKDF-SHA256( PBKDF2-SHA256(PIN, sal 16 B, 600 000 it.) ‖ secreto aleatorio de 32 B protegido con DPAPI del usuario )` |
| Cifrado de cada valor | ECIES: clave efímera ECDH P-256 → HKDF-SHA256 → AES-256-GCM (nonce 12 B, etiqueta 16 B). Formato `01 ‖ punto efímero 65 B ‖ nonce ‖ etiqueta ‖ texto cifrado`; en columnas de texto, `e1:` + Base64 |
| Qué se cifra | Textos de mensajes, borradores, nombres visibles y de PC de los contactos, nombres de stickers de la biblioteca, imágenes de stickers en disco (`.mcs`), documentos (`.mcf`) y sus nombres |
| Qué queda en claro | Huellas, IP/puerto de contactos, estados, fechas, contadores, el propio nombre visible (se difunde por la red de todos modos), hashes de stickers, ajustes |

Consecuencias del diseño:

- **Bloqueado se puede recibir**: cifrar solo necesita la clave pública. Descifrar (leer, enviar, reintentar,
  exportar) necesita la privada, que solo está en memoria tras introducir el PIN. Los envíos en cola esperan
  al desbloqueo y continúan solos.
- **Robo del archivo o de una copia de seguridad**: sin la cuenta de Windows del usuario no se puede abrir el
  secreto DPAPI, así que ni siquiera se puede intentar adivinar el PIN.
- **Quien controla la cuenta de Windows pero no conoce el PIN** tiene que adivinarlo con 600 000 iteraciones de
  PBKDF2 por intento (fuera de la app; dentro, además, hay esperas crecientes). Un PIN largo lo hace inviable;
  uno de 6 dígitos sigue siendo atacable con tiempo. Por eso se admiten frases de hasta 64 caracteres.
- **Cambiar el PIN** solo vuelve a proteger la clave privada (instantáneo). **PIN olvidado**: nueva bóveda; el
  historial anterior se elimina (no se puede descifrar); contactos, relaciones y bloqueos se conservan.
- **Datos anteriores** (perfiles creados por la versión sin cifrado): al crear el PIN se cifran textos,
  borradores, nombres y PNG; después `VACUUM` + `wal_checkpoint(TRUNCATE)` reescriben el archivo para que no
  queden páginas antiguas en claro. Los PNG antiguos se sobrescriben con ceros antes de borrarse (en SSD es un
  mejor esfuerzo). La migración de esquema v2 es solo aditiva y no hace copia de seguridad, para no dejar
  una copia sin cifrar.
- **Al bloquear** la interfaz vacía conversación, listas, miniaturas y borradores en memoria; la clave privada
  se descarta. Las imágenes se descifran en memoria y nunca se escriben descifradas en disco.
- **Carpeta de datos**: ACL protegida (sin herencia) con acceso solo para el usuario y SYSTEM. Un administrador
  podría tomar posesión (acción explícita y auditable), pero el contenido sigue cifrado.
- **Capturas de pantalla**: opción `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` para la ventana y el
  selector de stickers.

## Diagnóstico de red

`Engine/ChatEngine.Network.cs` + `Network/FirewallInspector.cs` (lectura de `HNetCfg.FwPolicy2`, API COM
documentada, sin modificar nada). Comprueba: interfaces sin IP válida (169.254), IP duplicada (detección de
Windows), red Pública o sin puerta de enlace (frecuente con IP fija), ninguna red autorizada, puertos ocupados,
reglas de bloqueo/permiso del firewall para el ejecutable en el perfil actual, máscaras distintas (los anuncios
incluyen el campo opcional `pl` con la longitud del prefijo) y ausencia de compañeros. Cada hallazgo incluye
causa y cómo resolverlo. «Probar conexión» abre una conexión TLS nueva con el contacto, verifica su huella y
mide el handshake y un `ping`. Las direcciones fuera de las redes autorizadas se rechazan antes de abrir ningún
socket y se cuentan (`NetworkPolicy.BlockedOutsideLan`).

## Actualizaciones

Código: [`Update/`](../src/MiniChat.Core/Update) (`UpdateService`, `AppVersionNumber`, tipos) y
`App/ViewModels/UpdateViewModel.cs`. Opcionales y manuales; no rompen la garantía de funcionar sin Internet.

- **Aparte de la red del chat**: un `HttpClient` propio que solo habla con GitHub (nunca pasa por
  `NetworkPolicy`/`PeerClient`). Si no hay Internet, falla en silencio.
- **Dos canales**: Estable (release normal) y Experimental (pre-release). Cada uno tiene su manifiesto
  `updates/<canal>.json` + `.sig` en el repo.
- **Confianza por firma**: se verifica la firma **ECDSA P-256** del manifiesto con la clave pública incrustada
  (`UpdateConfig.TrustedPublicKeyBase64`) antes de usar nada; el MSI se comprueba por **SHA-256 y tamaño** antes
  de ejecutarse. Solo se admiten URLs de descarga de GitHub. Comprometer GitHub no basta: hace falta la clave
  privada de firma, que no está ni en GitHub ni en la app. Sin clave configurada, la función queda desactivada.
- **Sin privilegios permanentes**: al pulsar Actualizar se lanza `msiexec` (UAC) y la app se cierra para dejarse
  reemplazar; no hay servicio ni tarea programada ni instalación silenciosa.
- **Aviso de «completada»**: al arrancar se compara la versión actual con la de la última ejecución
  (`update.lastRunVersion`); si subió, se avisa. Detecta también actualizaciones hechas por TI con el MSI.
- **Una sola instalación**: el instalador usa `UpgradeCode` fijo + `MajorUpgrade` (`AllowSameVersionUpgrades`),
  así cualquier MSI reemplaza al anterior (misma versión o distinta) y nunca quedan dos MiniChat. La versión se
  fija en `Directory.Build.props` y es la única fuente (app, MSI y nombre del archivo).

Procedimiento de publicación: [ACTUALIZACIONES.md](ACTUALIZACIONES.md).

## Interfaz

- Modo oscuro, Segoe UI, ~380×540 px lógicos, DPI por monitor (`PerMonitorV2`), redimensionable, contraíble
  (36 px), posición y tamaño guardados y recolocados dentro de un monitor visible al arrancar y al cambiar la
  configuración de pantallas.
- Pestañas Personas / Conversación / Ajustes (Ctrl+1/2/3). Enter envía, Mayús+Enter salta de línea. Borradores
  por conversación (memoria + base de datos). Texto seleccionable/copiable. Si el usuario lee mensajes antiguos
  no se desplaza: aparece «Nuevos mensajes ↓».
- Bandeja: mostrar/ocultar, Personas, Ajustes, silenciar, **Salir**. Cerrar/Alt+F4 oculta. Instancia única por
  perfil (la segunda pide a la primera que se muestre). Inicio con Windows opcional (`HKCU\...\Run`).
- Avisos discretos: icono de bandeja con punto, contador en el encabezado, sonido opcional y globo del sistema
  solo si el widget está oculto; nunca se activa la ventana ni se abre una conversación. Vista previa del texto
  desactivada por defecto.
- Con la ventana oculta o contraída no se reconstruyen listas; al ocultarla se libera la caché de imágenes y se
  devuelve memoria al sistema.

## Diagnóstico

`RollingFileLog`: 1 MB × 5 archivos. Registra eventos técnicos y huellas abreviadas; nunca textos de mensajes,
contenido de stickers ni claves.
