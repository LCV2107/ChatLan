# Dependencias, versiones y licencias

## Entorno

| Componente | Versión usada | Notas |
|---|---|---|
| .NET SDK | 10.0.401 (`global.json` exige 10.0.100+ de la banda 10.0) | LTS, soporte hasta noviembre de 2028 |
| Runtime | Microsoft.NETCore.App y Microsoft.WindowsDesktop.App 10.0.12 | Incluido en la publicación autocontenida del MSI |
| Destino | `net10.0-windows`, `win-x64` | Windows 10 y 11 x64 dentro de las versiones admitidas por .NET 10 (lista oficial: https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Verificado solo en Windows 11 Pro 10.0.26200 |
| TLS | SChannel de Windows | TLS 1.3 solo donde Windows lo admite (Windows 11); en Windows 10 se negocia TLS 1.2 |

## Paquetes que se distribuyen con la aplicación

| Paquete | Versión | Licencia | Para qué | Impacto en tamaño |
|---|---|---|---|---|
| Microsoft.Data.Sqlite (+ .Core) | 10.0.12 | MIT | Acceso a SQLite | ~0,2 MB |
| SQLitePCLRaw.bundle_e_sqlite3 (+ core, provider, lib) | 2.1.12 (transitivo) | Apache-2.0 | Motor SQLite nativo `e_sqlite3.dll` (SQLite: dominio público) | ~1,9 MB |
| SkiaSharp | 4.153.1 | MIT | Decodificar/validar PNG, JPG y WebP; normalizar a PNG | ~0,5 MB |
| SkiaSharp.NativeAssets.Win32 | 4.153.1 | MIT (+ avisos de Skia, libwebp, libjpeg-turbo, libpng, zlib…) | `libSkiaSharp.dll` nativo con los decodificadores | **~12,3 MB** (el mayor componente propio). Su `.pdb` de ~92 MB se excluye de la salida y del MSI |
| .NET runtime + WPF/WinForms | 10.0.12 | MIT | Ejecución autocontenida | El grueso de la publicación |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT | DPAPI: protege con la cuenta de Windows el secreto que, junto al PIN, abre el historial | 0 MB adicionales: la app usa la copia incluida en el runtime de escritorio |

Para el cifrado del historial **se descartó SQLCipher**: su paquete para .NET (`SQLitePCLRaw.bundle_e_sqlcipher`)
figura en NuGet como obsoleto («Legacy») y no pasa de la versión 2.1.11. Se usan las primitivas de .NET/Windows
(AES-GCM, ECDH, HKDF, PBKDF2 y DPAPI), que se mantienen con el propio runtime.

Publicación autocontenida medida (`dotnet publish -r win-x64 --self-contained -p:PublishReadyToRun=true`):
**279 archivos, 173,4 MB** sin comprimir. El MSI se comprime con CAB (tamaño final no medido: no se generó el
MSI). Alternativa no adoptada: publicación dependiente del runtime (~16 MB), que exige instalar antes el
.NET 10 Desktop Runtime en cada PC.

Textos de licencia y avisos: carpeta [`licenses/`](../licenses/README.md), copiada a `Licenses\` junto al
ejecutable e incluida en el MSI.

## Herramientas de compilación (no se distribuyen)

| Herramienta | Versión | Licencia |
|---|---|---|
| WiX Toolset SDK + WixToolset.Firewall.wixext | 6.0.2 | Código fuente MS-RL. Los **binarios** de NuGet se publican bajo el «Open Source Maintenance Fee Agreement» (OSMF EULA, aceptación requerida por el paquete): gratuito para uso que no genera ingresos; las organizaciones que generan ingresos con WiX deben pagar la cuota de mantenimiento del proyecto (o compilar WiX desde el código fuente). Revise el texto del EULA antes de usarlo en su organización. |
| xUnit v3 | 4.0.1 | Apache-2.0 |

## Mantenimiento

- SkiaSharp 4.x es una versión mayor reciente; se verificó su API real (decodificación, `FrameCount` para
  animaciones, `EncodedOrigin`) antes de usarla. Las actualizaciones de seguridad de los decodificadores llegan
  con nuevas versiones del paquete nativo: conviene actualizarlo periódicamente.
- Microsoft.Data.Sqlite sigue el ciclo de .NET 10 (parches mensuales).
- Para actualizar el runtime incluido basta recompilar el MSI con un SDK 10.0.x más reciente.
