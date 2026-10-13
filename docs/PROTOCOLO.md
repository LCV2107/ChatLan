# Protocolo MiniChat LAN (versión 1)

Valores centralizados en `src/MiniChat.Core/Config/MiniChatOptions.cs`.

## 1. Descubrimiento (UDP)

- Grupo `239.255.77.77`, puerto `47820`, TTL 1, un socket por interfaz autorizada.
- Datagrama = JSON UTF-8, máximo 1024 bytes:

```json
{"p":"mclan","k":"ann","min":1,"max":1,"iid":"<instancia>","fp":"<huella 64 hex>","dn":"Ana","pc":"PC-ANA","port":47821,"pl":24}
```

| `k` | Uso |
|---|---|
| `ann` | Anuncio periódico (10 s + 0–2 s aleatorios) y al cambiar el nombre |
| `qry` | Consulta al arrancar; los demás responden con `ann` (como mucho 1 respuesta cada 2 s) |
| `bye` | Salida ordenada: deja de figurar como disponible al instante |
| `qry` + `"u":1` | Sondeo **unicast** a otra subred autorizada. Solo `p`, `k`, `iid` y `u`: sin nombres ni huella. Se responde con un `ann` unicast al remitente (máximo 1 cada 5 s por IP) |

- `pl` (opcional, añadido sin cambiar la versión): longitud del prefijo de red de la interfaz que envía; sirve
  para avisar de máscaras de subred distintas. Los equipos que no lo envían o no lo entienden lo ignoran.
- `u` (opcional, añadido sin cambiar la versión): las versiones que no lo conocen lo ignoran y descartan el
  datagrama por venir de fuera de su subred.
- A los equipos encontrados en otra subred, `ann` y `bye` se les envían además por unicast al puerto 47820.
- Presencia: caduca a los 35 s sin anuncios (barrido cada 5 s). Máximo 256 equipos y 4 direcciones por huella.
- Límites: 100 datagramas/s en total y 10/s por IP; lo que exceda se descarta.
- Se ignoran: datagramas fuera de la subred de la interfaz (salvo de subredes de otras áreas autorizadas), protocolo distinto, campos inválidos, la propia
  instancia. Nunca contienen conversaciones ni claves privadas, y **no** prueban la identidad: la huella
  anunciada solo indica a quién intentar conectar; TLS la verifica.

## 2. Transporte

TCP `47821` + TLS 1.2/1.3 con certificado de cliente obligatorio (ver ARQUITECTURA.md). Tiempos: conexión 5 s,
handshake TLS y de aplicación 10 s, respuesta 15 s, inactividad 60 s (el cliente cierra antes, a 40 s).
Límites del servidor: 32 conexiones, 4 por IP, 20 tramas/s por conexión, 3 tramas mal formadas → cierre.

### Trama

```
uint32 BE   L = longitud del resto           2 ≤ L ≤ 1 572 864 (1,5 MiB)
uint16 BE   H = longitud de la cabecera      2 ≤ H ≤ 16 384,  H ≤ L − 2
H bytes     cabecera JSON UTF-8 con discriminador "op"
L−2−H bytes cuerpo binario opcional (PNG de un sticker)
```

El lector usa lecturas exactas: no supone que cada lectura del socket trae una trama completa. Corte a mitad
de trama → `EndOfStreamException` (desconexión). Longitud imposible → error y cierre. JSON inválido u `op`
desconocido → respuesta `error/malformed` y la conexión sigue (hasta 3).

Campos comunes: `id` (identificador único de la trama) y `re` (id de la trama a la que responde).

## 3. Operaciones

El cliente inicia todas las operaciones; el servidor solo responde (modelo petición/respuesta).

| Petición (`op`) | Campos | Respuesta |
|---|---|---|
| `hello` (obligatoria primero) | `proto`="mclan", `min`, `max`, `iid`, `dn`, `pc`, `port`, `app` | `hello_ok` {`ver`, `iid`, `dn`, `pc`, `port`, `app`} o `error/incompatible_version` {`min`,`max`} |
| `chat_request` | `rid` (GUID) | `request_status` {`rid`, `status`: pending · accepted · already_authorized · rejected · unavailable · rate_limited} |
| `request_decision` | `rid`, `decision`: accepted · rejected | `ack` {`status`: ok · cancelled · unknown_request} |
| `cancel_request` | `rid` | `ack` {`status`: ok} |
| `msg` | `mid` (GUID estable), `seq`, `sent` (ms Unix, informativo), `kind`: text · sticker, `text` (1–4000 caracteres) **o** `sticker` {`hash`, `w`, `h`, `len`} + cuerpo PNG | `msg_ack` {`mid`, `status`: stored · duplicate · not_authorized · invalid · too_large · storage_error} |
| `ping` | — | `pong` |
| `file_offer` (v2) | `fid` (GUID, = uid del mensaje), `name`, `size` (≤ 100 MB), `sha` (SHA-256 hex), `seq`, `sent` | `msg_ack` {`mid` = fid, `status`} |
| `file_get` (v2, la envía el **receptor**) | `fid`, `off`, `len` (≤ 1 MB) | `file_data` {`fid`, `off`, `last`} + bytes, o `error` {`file_locked` · `file_cancelled` · `file_unavailable`} |
| `file_done` (v2, receptor → emisor) | `fid` | `ack` |
| `file_reject` (v2, receptor → emisor) | `fid` | `ack` |
| `file_cancel` (v2, emisor → receptor) | `fid` | `ack` |

Documentos: el emisor ofrece; **solo si el usuario receptor acepta**, su equipo pide trozos en orden. La
reanudación tras un corte es pedir desde el último byte guardado. El emisor comprueba en cada `file_get` que
la identidad TLS es la del contacto autorizado de esa conversación. `file_locked` indica que el emisor tiene la
app bloqueada (no puede descifrar su copia): el receptor espera y reintenta. Al terminar, el receptor verifica
el SHA-256 completo antes de enviar `file_done`.

Errores genéricos (`error` {`code`, `msg`}): `incompatible_version`, `malformed`, `unsupported_op`,
`too_large`, `rate_limited`, `handshake_required`, `internal`.

Notas:

- `unavailable` se usa también cuando el receptor ha bloqueado al emisor (no se revela el bloqueo).
- `stored` y `duplicate` significan que el receptor **ya tiene guardado** el mensaje (commit con
  `synchronous=FULL`) antes de responder.
- La autorización se comprueba en el receptor con la huella demostrada por TLS, en cada `msg`.

## 4. Versionado y compatibilidad

- Cada lado anuncia el rango `[min, max]` que entiende (hoy `[1, 2]`; la v2 añade los documentos). Se usa
  `v = min(maxA, maxB)` si `v ≥ max(minA, minB)`; si no, `incompatible_version` con el rango local y la
  interfaz muestra «Actualicen MiniChat en ambos equipos». Los anuncios UDP llevan el mismo rango y la lista de
  personas marca a los equipos incompatibles.
- Reglas para versiones futuras: añadir campos opcionales no rompe la v1 (el deserializador ignora campos
  desconocidos); una operación nueva requiere subir `max` y no enviarla a pares con `ver` menor.
