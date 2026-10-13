# Actualizaciones (canal Estable y Experimental)

MiniChat puede comprobar si hay una versión nueva en **GitHub (repo público)** e instalarla con un botón. Es
**opcional y manual**: la app funciona sin Internet; el check solo ocurre si hay red y el usuario lo pide o deja
activado el aviso al arrancar. No hay instalación silenciosa ni servicios con privilegios.

## Cómo funciona (resumen técnico)

- La app descarga de `https://raw.githubusercontent.com/<repo>/<rama>/updates/<canal>.json` el **manifiesto**
  y su firma `.json.sig`.
- Verifica la **firma ECDSA P-256** con la clave pública incrustada (`UpdateConfig.TrustedPublicKeyBase64`)
  **antes de mirar nada**. Si no cuadra, se ignora.
- Compara la versión del manifiesto con la instalada (SemVer: `1.0.1`, `1.1.0-beta.1`…).
- Si hay una más nueva, descarga el MSI de la URL del manifiesto (solo se admiten URLs de GitHub), comprueba su
  **SHA-256 y tamaño**, y solo entonces lanza `msiexec` (con UAC). La app se cierra para dejarse reemplazar.
- Al reabrir ya actualizada, avisa «Actualización completada». Ese aviso también aparece si la instaló TI con el MSI.

**Garantía de confianza:** aunque se comprometa la cuenta de GitHub, nadie puede ofrecer una actualización sin
la **clave privada de firma**, que nunca está en GitHub ni en la app. El HTTPS protege el transporte; la firma
protege el contenido.

Código: [`src/MiniChat.Core/Update`](../src/MiniChat.Core/Update) (servicio, tipos, comparación de versiones) y
[`UpdateViewModel`](../src/MiniChat.App/ViewModels/UpdateViewModel.cs) (interfaz y lanzamiento del instalador).

## Activar las actualizaciones (una sola vez)

1. Genera la clave de firma (requiere **PowerShell 7**):

   ```powershell
   pwsh tools\firmar-release.ps1 -Init
   ```

   Crea `tools\keys\update-signing.key` (clave **privada**, ya en `.gitignore` — **no la subas nunca**) e
   imprime la clave **pública**.

2. Pega los dos valores en [`src/MiniChat.Core/Update/UpdateTypes.cs`](../src/MiniChat.Core/Update/UpdateTypes.cs):

   ```csharp
   public const string Repository = "tu-usuario/tu-repo";
   public const string TrustedPublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...";
   ```

3. Recompila. Mientras estos valores estén vacíos o con `OWNER/REPO`, el actualizador queda **desactivado**
   (no descarga ni ejecuta nada), y Ajustes lo indica.

> Guarda la clave privada en un lugar seguro y **haz copia**. Si la pierdes, tendrás que generar otra, cambiar
> la pública en el código y publicar una versión nueva por los medios habituales (los clientes antiguos no
> aceptarán manifiestos firmados con la clave nueva hasta actualizarse una vez a mano).

## Publicar una versión (planificado, trazado y anunciado)

1. **Sube la versión** en `Directory.Build.props` (`<Version>`): `1.0.1` para Estable, `1.1.0-beta.1` para
   Experimental. Haz commit.
2. **Compila el MSI**:

   ```powershell
   dotnet build installer\MiniChat.Installer\MiniChat.Installer.wixproj -c Release
   ```

3. **Crea la Release en GitHub** con el tag `v<versión>` y **adjunta el MSI** como asset. Para el canal
   Experimental, marca la Release como **«pre-release»**.
4. **Firma el manifiesto** apuntando a ese asset:

   ```powershell
   # Estable
   pwsh tools\firmar-release.ps1 -Msi <ruta>\MiniChatLAN-1.0.1-x64.msi -Version 1.0.1 -Channel stable -Repo tu-usuario/tu-repo -Notes "Correcciones menores."
   # Experimental
   pwsh tools\firmar-release.ps1 -Msi <ruta>\MiniChatLAN-1.1.0-beta.1-x64.msi -Version 1.1.0-beta.1 -Channel experimental -Repo tu-usuario/tu-repo
   ```

5. **Publica el manifiesto** (este push es el «lanzamiento» real para las apps):

   ```powershell
   git add updates\stable.json updates\stable.json.sig
   git commit -m "Release 1.0.1"
   git push
   ```

A partir de ahí, las apps del canal correspondiente ofrecerán la actualización al comprobar (al arrancar o con
el botón). `raw.githubusercontent.com` puede tardar unos minutos en servir el commit nuevo.

## Canales

- **Estable**: lo que usa la gente. Fixes pequeños. Release normal.
- **Experimental**: tus cambios en pruebas antes de desplegar. Release marcada como pre-release. Cada usuario lo
  activa en Ajustes › Actualizaciones; por defecto todos están en Estable.

## Firma del MSI (Authenticode, opcional)

La firma del **manifiesto** ya protege la integridad. Authenticode solo evita el aviso «editor desconocido» del
UAC. Opciones sin coste en una oficina con dominio: una **CA interna** (AD CS) o un certificado **autofirmado**
desplegado como «editor de confianza» por GPO. Un certificado comercial solo hace falta para equipos fuera del
dominio.

## Qué NO hace

- No se instala en silencio ni en segundo plano: siempre lo inicia el usuario y pasa por el UAC.
- No añade servicios, tareas programadas ni componentes con privilegios permanentes.
- No envía telemetría: solo pide los dos archivos del manifiesto y, si actualizas, el MSI.
