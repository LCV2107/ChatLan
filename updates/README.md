# Manifiestos de actualización

Esta carpeta contiene los manifiestos **firmados** que la app descarga para saber si hay una versión nueva:

| Archivo | Qué es |
|---|---|
| `stable.json` / `stable.json.sig` | Última versión del canal **Estable** y su firma |
| `experimental.json` / `experimental.json.sig` | Última versión del canal **Experimental** (pre-release) y su firma |

Los genera `tools/firmar-release.ps1`. La app (`UpdateService`) verifica la firma ECDSA P-256 con la clave
pública incrustada **antes** de mirar nada, y comprueba el SHA-256 del MSI descargado antes de ejecutarlo.

No edites estos archivos a mano: cualquier cambio invalida la firma y la app lo rechazará (es justo lo que
debe pasar). Para publicar una versión, mira el procedimiento en `docs/ACTUALIZACIONES.md`.
