<#
.SYNOPSIS
  Publica una versión de MiniChat LAN con UN SOLO comando: sube la versión, compila el MSI, crea la Release en
  GitHub con el MSI adjunto, firma el manifiesto y hace commit + push de updates\<canal>.json(.sig).

  Tú solo decides la VERSIÓN y las NOTAS. La clave privada de firma es tuya y nunca sale de este equipo
  (debe existir ya: pwsh tools\firmar-release.ps1 -Init).

  REQUIERE: PowerShell 7 (pwsh) y, salvo con -SinGitHub, la CLI de GitHub (gh) autenticada (gh auth login).

.EXAMPLE
  pwsh tools\publicar-release.ps1 -Version 1.0.0 -Notes "Primera versión."
  pwsh tools\publicar-release.ps1 -Version 1.0.1 -Channel stable -Notes "Correcciones menores."
  pwsh tools\publicar-release.ps1 -Version 1.1.0-beta.1 -Channel experimental -Notes "Prueba de otras subredes."
  pwsh tools\publicar-release.ps1 -Version 1.0.1 -DryRun          # muestra el plan, no toca nada
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [ValidateSet('stable', 'experimental')]
    [string]$Channel = 'stable',
    [string]$Notes = '',
    [string]$Repo,
    [string]$KeyFile,
    [switch]$SinGitHub,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Ejecute este script con PowerShell 7 (pwsh).' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsFile = Join-Path $repoRoot 'Directory.Build.props'
$wixproj = Join-Path $repoRoot 'installer\MiniChat.Installer\MiniChat.Installer.wixproj'
if (-not $KeyFile) { $KeyFile = Join-Path $repoRoot 'tools\keys\update-signing.key' }

function Step($n) { Write-Host "==> $n" -ForegroundColor Cyan }

# ---- Validaciones ----
if ($Version -notmatch '^[0-9]+(\.[0-9]+){0,3}(-[0-9A-Za-z.]+)?$') { throw "Versión no válida: '$Version'. Ejemplos: 1.0.0  o  1.1.0-beta.1" }
if ($Channel -eq 'experimental' -and $Version -notmatch '-') { Write-Warning "Experimental suele llevar etiqueta de pre-release (p. ej. $Version-beta.1)." }
if ($Channel -eq 'stable' -and $Version -match '-') { Write-Warning "El canal estable no suele llevar etiqueta de pre-release ($Version)." }
if (-not (Test-Path $KeyFile)) { throw "No hay clave de firma en $KeyFile. Créela una vez con: pwsh tools\firmar-release.ps1 -Init" }

# ---- Repositorio (de -Repo o del remoto git) ----
if (-not $Repo) {
    $url = (& git -C $repoRoot remote get-url origin 2>$null)
    if ($url -match 'github\.com[:/]+([^/]+)/([^/.]+)') { $Repo = "$($Matches[1])/$($Matches[2])" }
    else { throw 'No se pudo deducir el repositorio. Indíquelo con -Repo dueño/repo.' }
}

$tag = "v$Version"
$msiName = "MiniChatLAN-$Version-x64.msi"
$relNotes = if ($Notes) { $Notes } else { "MiniChat LAN $Version" }
$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()

Write-Host ''
Write-Host "Plan de publicación" -ForegroundColor Green
Write-Host "  Versión : $Version      (se escribirá en Directory.Build.props)"
Write-Host "  Canal   : $Channel"
Write-Host "  Repo    : $Repo   (rama $branch)"
Write-Host "  Tag/MSI : $tag  ·  $msiName"
Write-Host "  GitHub  : $([bool](-not $SinGitHub)) (crear Release y subir el MSI)"
Write-Host "  Firma   : $KeyFile"
Write-Host ''

if ($DryRun) { Write-Host 'DryRun: no se cambió nada.' -ForegroundColor Yellow; return }

if (-not $SinGitHub) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'No se encontró la CLI de GitHub (gh). Instálela, o use -SinGitHub.' }
    & gh auth status 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La CLI de GitHub no está autenticada. Ejecute: gh auth login' }
}

# ---- 1) Subir la versión ----
Step "Fijando la versión $Version en Directory.Build.props"
$props = Get-Content -Raw $propsFile
$propsNew = [regex]::Replace($props, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
if ($propsNew -eq $props -and $props -notmatch "<Version>$([regex]::Escape($Version))</Version>") { throw 'No se encontró <Version> en Directory.Build.props.' }
[System.IO.File]::WriteAllText($propsFile, $propsNew)

# ---- 2) Compilar el MSI ----
Step 'Compilando el MSI (Release)'
& dotnet build $wixproj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del MSI.' }

$msi = Get-ChildItem (Join-Path $repoRoot 'installer\MiniChat.Installer\bin') -Recurse -Filter $msiName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $msi) { throw "No se encontró $msiName tras compilar. Revise la salida del build." }
Write-Host "    MSI: $($msi.FullName)  ($([math]::Round($msi.Length/1MB,1)) MB)"

# ---- 3) Crear la Release en GitHub con el MSI ----
if (-not $SinGitHub) {
    Step "Creando la Release $tag en GitHub y subiendo el MSI"
    $ghArgs = @('release', 'create', $tag, $msi.FullName, '--repo', $Repo, '--title', "MiniChat LAN $Version", '--notes', $relNotes)
    if ($Channel -eq 'experimental') { $ghArgs += '--prerelease' }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh no pudo crear la Release $tag (¿ya existe ese tag?)." }
}

# ---- 4) Firmar el manifiesto ----
Step "Firmando el manifiesto del canal $Channel"
& (Join-Path $PSScriptRoot 'firmar-release.ps1') -Msi $msi.FullName -Version $Version -Channel $Channel -Repo $Repo -Notes $Notes -KeyFile $KeyFile

# ---- 5) Commit + push ----
Step 'Publicando versión y manifiesto (git)'
$jsonRel = "updates/$Channel.json"
& git -C $repoRoot add 'Directory.Build.props' $jsonRel "$jsonRel.sig"
& git -C $repoRoot commit -m "Release $Version ($Channel)"
if ($LASTEXITCODE -ne 0) { throw 'Falló el commit (¿nada que confirmar o conflicto?).' }

if (-not $SinGitHub) {
    & git -C $repoRoot push
    if ($LASTEXITCODE -ne 0) { throw 'Falló el push. Revise el remoto/credenciales y reintente «git push».' }
    Write-Host ''
    Write-Host "Listo. Versión $Version publicada en el canal $Channel." -ForegroundColor Green
    Write-Host 'Las apps lo verán al comprobar actualizaciones (raw.githubusercontent puede tardar unos minutos).'
} else {
    Write-Host ''
    Write-Host 'Hecho sin GitHub. Faltan dos pasos manuales:' -ForegroundColor Yellow
    Write-Host "  1) Cree la Release $tag en GitHub y adjunte el MSI:"
    Write-Host "       $($msi.FullName)"
    if ($Channel -eq 'experimental') { Write-Host '     (márquela como pre-release)' }
    Write-Host '  2) Suba el commit:  git push'
}
