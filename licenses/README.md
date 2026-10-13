# Licencias de terceros incluidas en MiniChat LAN

MiniChat LAN se distribuye junto con estos componentes. Los textos completos están en esta carpeta
(se copian a `Licenses\` junto al ejecutable y dentro del instalador MSI).

| Componente | Versión | Licencia | Archivo |
|---|---|---|---|
| .NET Runtime (incluido en la publicación autocontenida) | 10.0.12 | MIT | `dotnet-runtime-LICENSE.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` |
| .NET Windows Desktop Runtime (WPF, Windows Forms) | 10.0.12 | MIT | `dotnet-windowsdesktop-LICENSE.txt` |
| SkiaSharp + SkiaSharp.NativeAssets.Win32 (libSkiaSharp) | 4.153.1 | MIT | `SkiaSharp-LICENSE.txt` |
| Skia y bibliotecas incluidas en libSkiaSharp (libwebp, libjpeg-turbo, libpng, zlib, etc.) | — | Varias permisivas (BSD, IJG, zlib…) | `SkiaSharp-THIRD-PARTY-NOTICES.txt` |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | `Microsoft.Data.Sqlite-LICENSE.txt` |
| System.Security.Cryptography.ProtectedData (parte de .NET) | 10.0.12 | MIT | `dotnet-runtime-LICENSE.txt` |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, lib) — Copyright 2014-2024 SourceGear, LLC | 2.1.12 | Apache-2.0 | `Apache-2.0.txt` |
| SQLite (e_sqlite3.dll, compilado por SQLitePCLRaw) | — | Dominio público | https://sqlite.org/copyright.html |

Herramientas usadas solo para compilar (no se distribuyen dentro de la aplicación):
WiX Toolset 6.0.2 (fuente MS-RL; binarios bajo "Open Source Maintenance Fee EULA") y xUnit v3 (Apache-2.0).
