# Pruebas, mediciones y procedimiento entre dos PCs

Entorno de las pruebas: una sola PC, Windows 11 Pro 10.0.26200 x64, 20 procesadores lógicos, dos monitores
1920×1080 al 100 %, .NET SDK 10.0.401 / runtime 10.0.12. Red: Ethernet 10.10.160.0/24 con perfil
**Público** de Windows (por eso, en las pruebas manuales, la red se autorizó explícitamente en los perfiles de prueba).

> **Alcance real.** Todo se verificó en **una PC**: entre motores en el mismo proceso, entre procesos
> distintos y entre dos instancias de la aplicación real. **No se probó entre dos computadoras físicas.**
> No se afirma que la red real entre PCs quede validada: use el [procedimiento](#procedimiento-de-prueba-entre-dos-pcs).

## 1. Pruebas automatizadas (153, todas superadas)

```powershell
dotnet test --project tests\MiniChat.Tests
```

Última ejecución: 153 correctas, 0 errores (dos ejecuciones seguidas sin fallos). Tras corregir los problemas de la sección 5, la batería se
ejecutó más de 10 veces seguidas sin ningún fallo.

Tipos: **[U]** unidad · **[P]** dos motores reales en el mismo proceso (TLS, SQLite, claves CNG y multicast
reales por loopback) · **[M]** procesos distintos (`MiniChat.Peer.exe`).

| Área | Pruebas |
|---|---|
| Aceptación / recorrido completo | [P] descubrir → solicitar → aceptar → texto en ambos sentidos → cerrar ambos → reabrir con historial y autorización → enviar de nuevo. [M] lo mismo entre dos procesos, con sticker en ambos sentidos, cierre abrupto (`Kill`) del receptor, envío fallido visible, reinicio y reintento manual. |
| Rechazo | [P] rechazar no autoriza; ninguno de los dos puede escribir; reintento inmediato no vuelve a avisar. |
| Autorización en el receptor | [P] paquetes de mensaje enviados «a mano» (sin la interfaz) sin autorización y con solicitud pendiente → `not_authorized` y nada guardado. |
| Solicitudes simultáneas | [P] ambos solicitan a la vez → ambos autorizados y pueden escribir. |
| Duplicadas | [P] 3 envíos de la misma solicitud → 1 aviso y 1 pendiente. |
| Desconexión antes de responder / aceptación perdida | [P] el solicitante cierra; el otro acepta; al volver, recibe la aceptación reentregada. [P] el solicitante cancela sin poder avisar; el otro acepta después → se deshace (sin autorización a medias). [P] cancelar antes de la respuesta. |
| Bloqueo | [P] bloquear corta mensajes (`RevokedByPeer` en el emisor) y solicitudes (sin aviso), conserva historial; desbloquear no reautoriza; reintento respeta la revocación. |
| Cambio de IP / dirección | [P] el par vuelve con otro puerto (equivalente a otra dirección) → mismo contacto. [U] misma huella con otra IP actualiza; misma PC y nombre con otra huella es otro contacto. |
| Cambio de identidad | [P] el par pierde su clave → identidad nueva detectada, marcada como posible cambio del contacto anterior, envío fallido con explicación, y la nueva identidad no puede escribir sin emparejarse. |
| Conexión manual por IP | [P] pares que no se ven por descubrimiento se encuentran por IP; IP fuera de la red autorizada, texto no válido y puerto cerrado dan errores claros. |
| Unicode | [P] español, emoji con ZWJ y banderas, RTL, combinantes, CJK, saltos de línea, 4000 caracteres; límites 4001 y vacío. [U] tramas Unicode entregadas byte a byte. |
| Desconexiones y reintentos | [P] destinatario apagado → fallido; los siguientes esperan; reintento entrega en orden. [P] sin confirmación del receptor (servidor «agujero negro» con la identidad real) → «No confirmado»; el reintento lo entrega sin duplicar. [P] cierre del emisor con «Enviando» → «No confirmado» al reabrir. [P] confirmación perdida → reenvío idéntico = `duplicate`, un solo mensaje. |
| Errores de almacenamiento | [P] fallo de escritura en el receptor (disparador SQLite) → «Fallido» con motivo, nunca «Entregado»; tras corregir, el reintento lo entrega. [U] transacción fallida no deja estado parcial. [U] archivo corrupto, páginas dañadas y versión futura → error sin modificar el archivo (comparado byte a byte). |
| Recuperación tras cierre inesperado | [M] 3 rondas de `Kill` durante escrituras continuas: `integrity_check = ok` y ningún lote de 3 inserciones a medias. |
| Paquetes malformados | [U] tramas fragmentadas, truncadas, longitudes imposibles. [P] JSON roto, operación desconocida, campos inválidos → error y cierre al tercero; el servidor sigue atendiendo. Trama de 2 GB declarada → cierre. [U] datagramas UDP basura, ajenos, de otra subred e inundación. |
| Compatibilidad de versiones | [U] negociación de rangos. [P] par que solo admite v7–v9 → `incompatible_version` con el rango local. |
| Stickers | [U] PNG con transparencia y proporción al reducir; WebP con alfa sin códecs de Windows; JPG sin ampliar; WebP **animado real** → primer fotograma e indicación; contenido falso con extensión de imagen, ejecutable, GIF, vacío, truncado, 5000 px, 6 MB y bomba de descompresión (60000×60000 declarados) rechazados. [P] biblioteca: deduplicación, favoritos, recientes, el receptor recibe la imagen, quitar de la biblioteca no borra la imagen usada por mensajes. [P] sticker manipulado en la red (hash, dimensiones, formato) rechazado. |
| Persistencia | [P] historial paginado (120 mensajes en 3 páginas, orden estable). [P] exportar texto + carpeta de stickers; borrar historial local no afecta al compañero. [P] borradores tras reiniciar. [U] esquema, WAL. |
| Cifrado del historial | [U] bóveda: PIN correcto/incorrecto, cifrar con la app bloqueada pero no leer, cambio de PIN, manipulación detectada, PIN débiles rechazados, destrucción irreversible. [P] **los textos, borradores y nombres de PC no aparecen en los bytes del archivo SQLite ni de su WAL**; con la app bloqueada se reciben y guardan mensajes pero no se leen; envíos en cola continúan al desbloquear; esperas crecientes tras PIN incorrectos (ni el PIN correcto entra durante la espera); PIN olvidado borra el historial y conserva contactos y autorización; un perfil de la versión sin cifrado se cifra entero al crear el PIN y no quedan restos en claro; stickers en disco sin firma PNG. [U] carpeta de datos con permisos solo para el usuario y SYSTEM. |
| Documentos adjuntos | [P] oferta → aceptación → transferencia por trozos → verificación SHA-256 → «Guardar como» idéntico al original con marca Zone.Identifier; documento y nombre cifrados en disco en ambos equipos; rechazo; cancelación del emisor; **pausa al bloquearse el emisor y reanudación desde el mismo punto** (6 MB en trozos de 16 KB); reanudación tras reiniciar receptor y emisor; un tercero con el identificador y la IP no obtiene nada; límite de tamaño; oferta recibida estando bloqueado; copia abierta borrada al bloquear. [U] saneado de nombres (incluido el truco de invertir la extensión), tipos peligrosos; par v1 sin operaciones de documentos. Interfaz real: ofertas de PDF y `.exe` recibidas, aceptadas, descargadas (3 MB) y advertencia antes de abrir el `.exe`. |
| Diagnóstico de red y sin Internet | [U] con inventario simulado: IP duplicada, IP fija sin puerta de enlace, red Pública, adaptador con 169.254, ninguna red autorizada y red sana; todos los problemas incluyen cómo resolverlos. [P] «Probar conexión» mide TLS real y `ping`; estado sin Internet cuenta compañeros; las IP de Internet (8.8.8.8, 1.1.1.1…) se rechazan antes de abrir conexión y se cuentan. |
| Otras subredes de la oficina | [U] subredes válidas (normalización, máscara en notación decimal) y rechazadas (Internet, 172.32/16, /8, /31, sin máscara, loopback). [U] con una oficina simulada (10.10.160.0/24, router hacia 10.10.0.0/16): otra subred rechazada hasta autorizarla, después aceptada al conectar y al recibir y solo esa; ruta por una red Pública rechazada; la lista no habilita una red conectada denegada. [U] anuncio de otra subred aceptado y marcado; de una subred no autorizada, ignorado. [U] orden del sondeo (últimas IP primero, sin la subred propia, la dirección de red ni la de difusión, sin repetidos). [P] sondeo unicast por loopback: respuesta directa y límite por origen; el sondeo no lleva nombre ni huella. [U] cobertura del firewall por dirección remota (`*`, LocalSubnet, máscaras, rangos, listas). [P] motor: validación, solapes, persistencia tras reabrir, subredes del administrador no editables y aviso de las no válidas. [P] «Probar una IP» rechaza Internet y texto no válido sin generar tráfico. |
| Actualizaciones | [U] comparación de versiones SemVer (núcleo, pre-release menor que final, numérico, cuarto campo) y rechazo de versiones mal formadas. [U] URL de instalador: solo HTTPS de GitHub. [U] servicio con firma ECDSA real y HTTP simulado: encuentra versión nueva con firma válida; rechaza manifiesto manipulado, firmado con otra clave o con URL ajena a GitHub; «sin red» se informa sin romper; descarga verifica SHA-256 y tamaño y descarta lo que no cuadra. Verificado aparte: roundtrip de firma PowerShell (firmar-release.ps1) ↔ verificador .NET. |
| Red, identidad, registro | [U] política de redes (privada/pública/VPN/denegada/loopback) y lectura real del perfil de red de Windows. [P] TLS mutuo identifica por huella; huella distinta rechazada; red no autorizada rechazada antes de enviar nada. [U] clave CNG persistente y no exportable; código de emparejamiento simétrico; rotación del registro. |

## 2. Verificación de la aplicación real (una PC, dos procesos)

Dos instancias de `MiniChat.exe` (perfiles `ana` y `bea`) manejadas con UI Automation, sin mover el ratón:

- Descubrimiento por la interfaz Ethernet real; ambos ven el mismo código de emparejamiento.
- Solicitud → aceptación → texto con emoji/ñ/saltos de línea en ambos sentidos → «Entregado».
- Importación de stickers (PNG, WebP, JPG; un «PNG» falso rechazado por contenido; duplicado detectado) y envío
  de un WebP con transparencia, mostrado en el receptor que no lo tenía.
- `Stop-Process -Force` de una instancia y reapertura: contacto «Autorizado» sin nueva solicitud e historial
  completo con el sticker.
- Ráfaga de 60 mensajes: desplazamiento automático al estar al final; leyendo historial antiguo la posición
  se mantiene (30 % → 29,5 %) y aparece «Nuevos mensajes ↓», que lleva al final.
- Segunda instancia del mismo perfil: termina (código 0) y la primera se muestra.
- Contraer (36 px) / expandir (540 px); inicio con Windows crea y elimina `HKCU\...\Run\MiniChatLAN-bea`.
- Posición guardada fuera de los monitores → recolocada en el monitor más cercano.
- Reinicio de Explorer simulado (propietario a 0 + `TaskbarCreated`) → reenganche al escritorio.

### Integración con el escritorio (sonda)

- Prototipo de la fase 1 (misma técnica que la app final): **7/7 comprobaciones superadas** en modo widget
  (detalle en [ESCRITORIO.md](ESCRITORIO.md)); la estrategia alternativa por hook falló Win+D y se descartó.
- App final: la sonda **no pudo completarse**. Durante la sesión quedaron pendientes avisos del Firewall de
  Windows («¿Permitir… en redes públicas?», mostrados porque los ejecutables de desarrollo escuchaban en la red
  Pública autorizada para las pruebas). Esos diálogos atenúan la pantalla (`Shell_SystemDim`) en cuanto se
  activan ventanas, por lo que todas las comprobaciones de entrada vieron esa capa. No se respondieron los
  diálogos porque modificar el firewall corresponde al usuario. Tras responderlos, repetir:

```powershell
dotnet build tools\MiniChat.DesktopProbe -c Release
dotnet build src\MiniChat.App -c Release
tools\MiniChat.DesktopProbe\bin\Release\net10.0-windows\win-x64\MiniChat.DesktopProbe.exe src\MiniChat.App\bin\Release\net10.0-windows\win-x64\MiniChat.exe DesktopWidget
tools\MiniChat.DesktopProbe\bin\Release\net10.0-windows\win-x64\MiniChat.DesktopProbe.exe src\MiniChat.App\bin\Release\net10.0-windows\win-x64\MiniChat.exe CompactWindow
```

La sonda pulsa Win+D (minimiza sus ventanas durante unos segundos y luego las restaura), hace clic y escribe en
la caja de nombre del perfil `probe`. En modo `CompactWindow` es **esperable** que falle «tras Win+D el widget
es visible»: ese modo no tiene integración.

## 3. Mediciones de memoria y CPU

Compilación Release (dependiente del runtime instalado), dos instancias en la misma PC, valores de
`Get-Process` (conjunto de trabajo, bytes privados y tiempo de CPU acumulado en el intervalo). CPU expresada en
segundos de CPU por intervalo; 1 s en 60 s ≈ 1,7 % de un núcleo.

| Escenario (60 s) | CPU | Privada | Conjunto de trabajo | Hilos |
|---|---|---|---|---|
| Reposo, ventana visible, conversación abierta | 0,05 – 0,39 s | 81 – 83 MB | 164 MB | 22 |
| Oculta en la bandeja | 0,00 – 0,06 s | 76 – 78 MB | 168 MB | 21 |
| Receptor de 60 mensajes (1/s) con la conversación abierta; emisor sin interfaz (`MiniChat.Peer`) | 2,1 s (≈35 ms por mensaje: TLS, guardado con `fsync`, renderizado) | 100 MB | 191 MB | — |

Notas:

- El conjunto de trabajo incluye páginas compartidas del runtime y de WPF; los bytes privados son la cifra
  representativa del consumo propio.
- Al ocultar, la caché de imágenes se vacía y se ejecuta una recolección agresiva (devuelve memoria al sistema).
- Una medición del emisor manejado por UI Automation (9,2 s en 60 s) **no es representativa**: UI Automation
  recorre el árbol de controles en cada búsqueda y carga el proceso medido. Esa medición sirvió para detectar
  dos ineficiencias reales que se corrigieron (cada anuncio de presencia y cada mensaje recargaban la lista de
  personas); tras corregirlas, el receptor bajó de 4,5 s a 2,1 s.
- No se midió en un equipo modesto ni en Windows 10.

## 4. Funcionamiento sin Internet

- El código no contiene direcciones de servicios externos; no hay telemetría ni actualizaciones.
- TLS usa `DisableCertificateDownloads = true` y sin revocación: no consulta CRL/OCSP/AIA.
- Todas las pruebas automatizadas usan loopback y no requieren Internet.
- Las direcciones que no pertenecen a una red local autorizada se rechazan antes de abrir ningún socket
  (pruebas con 8.8.8.8, 1.1.1.1 y 142.250.0.1).
- No se desconectó la red de la PC de pruebas (era la sesión de trabajo del usuario).

### Cómo demostrarlo sin tocar el resto del equipo

`tools\prueba-sin-internet.ps1` crea dos reglas del firewall de Windows que impiden a **MiniChat.exe, y solo a
él**, comunicarse con cualquier dirección fuera de las subredes locales del equipo (además de bloquear todo
IPv6). Se permiten únicamente la red local, el propio equipo y el multicast local del descubrimiento. El resto
de programas siguen con Internet.

```powershell
# Ver qué se bloquearía, sin cambiar nada (no requiere administrador)
.\tools\prueba-sin-internet.ps1 -Simular
# Activar (PowerShell como administrador, en ambas PCs)
.\tools\prueba-sin-internet.ps1 -Exe "C:\Program Files\MiniChat LAN\MiniChat.exe"
# Comprobar y deshacer
.\tools\prueba-sin-internet.ps1 -Estado
.\tools\prueba-sin-internet.ps1 -Desactivar
```

Con la prueba activa, Ajustes › Prueba sin Internet muestra «Prueba activa». Entonces se repite el recorrido:
descubrir, solicitar, aceptar, texto y sticker en ambos sentidos. La simulación en esta PC calculó como
permitida solo 10.10.160.0/24 (+ loopback y multicast) y bloqueó `0.0.0.0-10.10.159.255`,
`10.10.161.0-126.255.255.255`, `128.0.0.0-223.255.255.255`, `240.0.0.0-255.255.255.255` y todo IPv6.
Las reglas reales no se activaron en esta sesión (requieren administrador y modifican el firewall).
La prueba más contundente sigue siendo desconectar el cable de Internet del router (paso 17).

## 5. Problemas encontrados y corregidos durante las pruebas

| Problema | Corrección |
|---|---|
| Archivo SQLite ilegible quedaba bloqueado por una conexión huérfana | `OpenConnection` libera la conexión si falla la inicialización |
| Solicitudes cruzadas podían dejar a ambos en «saliente pendiente» | Recibir «pendiente» estando en «entrante pendiente» = consentimiento mutuo |
| El límite de 20 tramas/s del receptor marcaba como fallidos envíos rápidos | El emisor regula el ritmo al recibir `rate_limited` |
| Dos importaciones simultáneas del mismo contenido chocaban al escribir el PNG | Escritura serializada por hash |
| Recargas completas de la lista de personas por cada anuncio y cada mensaje | Solo se notifican cambios reales; contadores sin recargar la lista |
| La herramienta `MiniChat.Peer` leía stdin con la página de códigos OEM (ñ y emoji corruptos) | E/S en UTF-8 |
| Pruebas que elegían puertos del rango donde escuchan servicios de Windows (49664+) | Puertos libres asignados por el sistema |
| Instalador: validación ICE38/ICE43/ICE57 del acceso directo | KeyPath del acceso directo en HKCU |
| «Sin problemas» quedaba detrás de los avisos informativos en el diagnóstico | Orden corregido |
| El selector de minutos de bloqueo aparecía vacío (estilo de texto global sobre fondo claro) | Estilo propio del desplegable |

## Procedimiento de prueba entre dos PCs

Necesita dos PCs con Windows 10/11 x64 en la **misma subred** (mismo switch o la misma red Wi-Fi sin
aislamiento de clientes), y un teléfono u otro medio para comparar el código de emparejamiento.

### Preparación (en ambas PCs)

1. Instalar el MSI (recomendado: crea las reglas de firewall limitadas) **o** copiar la carpeta publicada
   (`artifacts\publish\MiniChat\`, o la salida de `dotnet publish src\MiniChat.App -c Release -r win-x64 --self-contained true`)
   y ejecutar `tools\firewall-dev.ps1 -Exe <ruta>\MiniChat.exe` como administrador.
2. Comprobar que la red figura como **Privada** (Configuración › Red e Internet › propiedades de la red). Si
   es Pública, cambiarla o autorizarla en MiniChat (Ajustes › Redes) sabiendo que el firewall seguirá
   bloqueando las conexiones entrantes en perfil Público.
3. Anotar la IP de cada PC (Ajustes › IP local).

### Pasos y resultado esperado

| # | Acción | Resultado esperado |
|---|---|---|
| 1 | Abrir MiniChat en A («Ana») y B («Beto»); escribir el nombre | Cada uno ve su nombre y el de su PC en el encabezado |
| 2 | Esperar ≤ 15 s en Personas | Cada uno ve al otro en «Disponibles en la red» con nombre, PC y punto verde |
| 3 | A pulsa «Solicitar chat» | A: «Solicitud enviada». B: aparece en Solicitudes con «Quiere chatear contigo» |
| 4 | Comparar por teléfono el código de emparejamiento | Los 24 dígitos son idénticos en ambas pantallas |
| 5 | B intenta abrir la conversación antes de aceptar | No hay botón «Abrir»; no se puede escribir |
| 6 | B pulsa «Aceptar» | Ambos ven «Autorizado» |
| 7 | A envía «Hola ñandú 😀» y una línea con Mayús+Enter | B lo recibe íntegro; en A pasa a «Entregado» |
| 8 | B responde | A lo recibe; en B «Entregado» |
| 9 | A importa un PNG con transparencia, un WebP y un JPG y envía uno | B ve el sticker con transparencia y proporciones |
| 10 | B envía un sticker WebP a A | A lo ve aunque no lo tuviera |
| 11 | Desconectar el cable/Wi-Fi de B; A envía un mensaje | Tras ≤ 35 s B figura «Desconectado»; el mensaje de A queda «Fallido» con motivo, el texto no se pierde |
| 12 | Reconectar B; esperar a verlo disponible; A pulsa «Reintentar» | El mensaje llega una sola vez; «Entregado» |
| 13 | Cerrar MiniChat en ambas (bandeja › Salir) y volver a abrir | Contacto «Autorizado» sin nueva solicitud; historial y stickers intactos |
| 14 | Cambiar la IP de B (renovar DHCP o IP fija distinta en la misma subred) | A sigue viendo un único contacto «Beto» y puede escribirle |
| 15 | A: Personas › «Conectar por IP» con la IP de B (con el descubrimiento funcionando o sin él) | «Encontrado: Beto (PC-…)» |
| 16 | A: «Conectar por IP» con una IP de otra subred | Mensaje indicando que no pertenece a su red y cómo añadir la subred |
| 16b | (Si hay otra subred en la oficina) A: Ajustes › Otras subredes › «Probar una IP» con la IP de C (otra subred) | Veredicto con ruta, ping, routers y puerto |
| 16c | Como administrador en A y C: `tools\firewall-subredes.ps1 -Subredes <subred del otro>`; reabrir MiniChat | La subred aparece «definida por el administrador»; en ≤ 1 min A y C se ven en Personas y pueden chatear |
| 17 | Desconectar el enlace a Internet del router (la LAN sigue) y repetir 7–8. Alternativa sin tocar el router: `tools\prueba-sin-internet.ps1` en ambas PCs | Funciona igual; Ajustes › Prueba sin Internet lo refleja |
| 17b | Ajustes › Redes › Diagnosticar red en ambas PCs | Sin errores; si los hay, aplicar la solución indicada |
| 17c | Bloquear con el candado en B; A envía un mensaje; desbloquear B | B lo recibe bloqueado (aviso sin remitente ni texto) y lo ve al desbloquear |
| 18 | B bloquea a A; A envía un mensaje y luego una solicitud | Mensaje «Fallido» («ya no acepta sus mensajes»); la solicitud no llega a B; el historial de B se conserva |
| 19 | B desbloquea | A sigue sin poder escribir hasta nueva solicitud y aceptación |
| 20 | En B: menú ⋯ › Exportar a texto; luego Borrar historial | Archivo con mensajes y `[Sticker: …png (w×h)]` + carpeta de imágenes; el historial de A no cambia |

Registrar en cada paso capturas y, si algo falla, el archivo `%LocalAppData%\MiniChatLAN\logs\minichat.log` de
ambas PCs (no contiene textos de mensajes). Para diagnosticar la red sin interfaz se puede usar
`MiniChat.Peer.exe --data-dir <carpeta> --name Diag --pin <PIN>` y las órdenes `networks`, `peers` y `connect <ip>`.

## Verificación manual de la integración con el escritorio

1. Abrir varias ventanas sobre el widget: deben cubrirlo.
2. Win+D: el widget debe verse sobre el fondo; hacer clic en su caja de texto y escribir con acentos y emoji;
   seleccionar con el ratón y copiar con Ctrl+C.
3. Hacer doble clic en un icono del escritorio fuera del widget: debe abrirse.
4. **Reinicio de Explorer**: Administrador de tareas › «Explorador de Windows» › Reiniciar. El widget debe
   seguir abierto y, tras unos segundos, seguir visible con Win+D (Ajustes › Escritorio muestra el estado). El
   icono de bandeja debe reaparecer.
5. Cambiar la resolución y el escalado (Configuración › Pantalla), y desconectar un monitor con el widget en
   él: el widget debe quedar entero dentro de un monitor y con el tamaño adecuado.

## Generar el instalador MSI

Prerrequisito: .NET SDK 10 y acceso a nuget.org la primera vez (descarga WiX 6.0.2 y los paquetes de runtime
para la publicación autocontenida). Desde la raíz del repositorio:

```powershell
dotnet build installer\MiniChat.Installer\MiniChat.Installer.wixproj -c Release
```
