<#
.SYNOPSIS
  Firma una release de MiniChat LAN para el actualizador. Genera (o usa) una clave ECDSA P-256 y produce el
  manifiesto firmado que la app descarga de GitHub: updates\<canal>.json  +  updates\<canal>.json.sig

  REQUIERE PowerShell 7 (pwsh), no Windows PowerShell 5.1.

  La clave PRIVADA se guarda en tools\keys\update-signing.key y NUNCA se sube a GitHub (está en .gitignore).
  La clave PÚBLICA se pega una sola vez en UpdateConfig.TrustedPublicKeyBase64 (src\MiniChat.Core\Update).

.EXAMPLE
  # 1) Una sola vez: crear la clave y obtener la pública para pegarla en el código.
  pwsh tools\firmar-release.ps1 -Init

  # 2) En cada release: firmar el MSI ya compilado.
  pwsh tools\firmar-release.ps1 -Msi artifacts\MiniChatLAN-1.0.1-x64.msi -Version 1.0.1 -Channel stable -Repo luiscv/minichat-lan -Notes "Correcciones menores."
  pwsh tools\firmar-release.ps1 -Msi artifacts\MiniChatLAN-1.1.0-beta.1-x64.msi -Version 1.1.0-beta.1 -Channel experimental -Repo luiscv/minichat-lan

  # Luego: subir el MSI como asset de la Release en GitHub y hacer commit/push de updates\<canal>.json(.sig).
#>
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(ParameterSetName = 'Init')]
    [switch]$Init,

    [Parameter(ParameterSetName = 'Sign', Mandatory = $true)]
    [string]$Msi,
    [Parameter(ParameterSetName = 'Sign', Mandatory = $true)]
    [string]$Version,
    [Parameter(ParameterSetName = 'Sign', Mandatory = $true)]
    [ValidateSet('stable', 'experimental')]
    [string]$Channel,
    [Parameter(ParameterSetName = 'Sign')]
    [string]$Repo,
    [Parameter(ParameterSetName = 'Sign')]
    [string]$Url,
    [Parameter(ParameterSetName = 'Sign')]
    [string]$Notes = '',
    [Parameter(ParameterSetName = 'Sign')]
    [string]$OutDir,

    [string]$KeyFile
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Ejecute este script con PowerShell 7 (pwsh), no con Windows PowerShell.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $KeyFile) { $KeyFile = Join-Path $repoRoot 'tools\keys\update-signing.key' }

function New-Ecdsa { [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256')) }

if ($Init) {
    if (Test-Path $KeyFile) { throw "Ya existe una clave en $KeyFile. Para no romper actualizaciones ya publicadas, no la sobrescriba. Bórrela a conciencia si de verdad quiere una nueva." }
    New-Item -ItemType Directory -Force (Split-Path $KeyFile) | Out-Null
    $ec = New-Ecdsa
    try {
        $pem = [System.Security.Cryptography.PemEncoding]::Write('PRIVATE KEY', $ec.ExportPkcs8PrivateKey())
        [System.IO.File]::WriteAllText($KeyFile, [string]::new($pem))
        $pub = [Convert]::ToBase64String($ec.ExportSubjectPublicKeyInfo())
    } finally { $ec.Dispose() }
    Write-Host "Clave privada creada en: $KeyFile  (NO la suba a GitHub; ya está en .gitignore)"
    Write-Host ''
    Write-Host 'Pegue esta clave PÚBLICA en src\MiniChat.Core\Update\UpdateTypes.cs (UpdateConfig.TrustedPublicKeyBase64):'
    Write-Host ''
    Write-Host "    public const string TrustedPublicKeyBase64 = `"$pub`";"
    Write-Host ''
    Write-Host 'Y ponga su repositorio en UpdateConfig.Repository (p. ej. "luiscv/minichat-lan"). Luego recompile.'
    return
}

if (-not (Test-Path $KeyFile)) { throw "No hay clave de firma en $KeyFile. Ejecute primero: pwsh tools\firmar-release.ps1 -Init" }
if (-not (Test-Path $Msi)) { throw "No se encontró el MSI: $Msi" }

# Validación de la versión (igual que AppVersionNumber): núcleo numérico y pre-release opcional.
if ($Version -notmatch '^[0-9]+(\.[0-9]+){0,3}(-[0-9A-Za-z.]+)?$') { throw "Versión no válida: '$Version'. Ejemplos: 1.0.1  o  1.1.0-beta.1" }
if ($Channel -eq 'experimental' -and $Version -notmatch '-') { Write-Warning "El canal experimental suele usar una etiqueta de pre-release (p. ej. $Version-beta.1)." }
if ($Channel -eq 'stable' -and $Version -match '-') { Write-Warning "El canal estable no suele llevar etiqueta de pre-release ($Version)." }

$msiItem = Get-Item $Msi
$sha = (Get-FileHash -Algorithm SHA256 $Msi).Hash.ToLowerInvariant()
$size = $msiItem.Length

if (-not $Url) {
    if (-not $Repo) { throw 'Indique -Repo dueño/repo (para construir la URL de descarga) o una -Url completa.' }
    $Url = "https://github.com/$Repo/releases/download/v$Version/$($msiItem.Name)"
}
if ($Url -notmatch '^https://([a-z0-9-]+\.)*github(usercontent)?\.com/') { throw "La URL debe ser https de GitHub: $Url" }

if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'updates' }
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Objeto en el MISMO orden/formato que espera el manifiesto (claves camelCase). Se firman los bytes exactos.
$manifest = [ordered]@{
    schema  = 1
    version = $Version
    channel = $Channel
    url     = $Url
    sha256  = $sha
    size    = $size
    notes   = $Notes
}
$json = ($manifest | ConvertTo-Json -Compress -Depth 4)
$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)

$ec = New-Ecdsa
try {
    $ec.ImportFromPem([System.IO.File]::ReadAllText($KeyFile))
    $sig = $ec.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
} finally { $ec.Dispose() }

$jsonPath = Join-Path $OutDir "$Channel.json"
$sigPath = "$jsonPath.sig"
[System.IO.File]::WriteAllBytes($jsonPath, $bytes)
[System.IO.File]::WriteAllText($sigPath, [Convert]::ToBase64String($sig))

Write-Host "Manifiesto firmado:"
Write-Host "  $jsonPath"
Write-Host "  $sigPath"
Write-Host ''
Write-Host "Versión $Version · canal $Channel · $([math]::Round($size/1MB,1)) MB · SHA-256 $sha"
Write-Host ''
Write-Host 'Pasos para publicar (planificado, trazado y anunciado):'
Write-Host "  1) Cree la Release en GitHub con el tag v$Version y adjunte el MSI como asset."
if ($Channel -eq 'experimental') { Write-Host '     Marque la Release como «pre-release».' }
Write-Host "  2) git add updates\$Channel.json updates\$Channel.json.sig && git commit && git push"
Write-Host '  3) A partir de ahí, las apps del canal lo ofrecerán al comprobar actualizaciones.'
