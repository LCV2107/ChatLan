<#
.SYNOPSIS
  Corta Internet SOLO a MiniChat para demostrar que funciona únicamente con la red local.

.DESCRIPTION
  Crea reglas del Firewall de Windows que bloquean a MiniChat.exe (y a nada más) cualquier dirección IPv4
  fuera de las subredes locales de este equipo, y todo IPv6. Se permiten solo:
    - las subredes de los adaptadores con IP de este equipo (su red local),
    - 127.0.0.0/8 (el propio equipo) y 224.0.0.0/4 (multicast local, usado por el descubrimiento).
  El resto de programas y del equipo siguen con Internet normalmente. No se modifica el router, ni los
  adaptadores, ni otras reglas. Para deshacerlo: -Desactivar.

  Así se demuestra que MiniChat descubre, empareja y envía mensajes y stickers aunque no pueda llegar a
  ninguna dirección de Internet. La prueba definitiva sigue siendo desconectar el cable de Internet del
  router (ver docs\PRUEBAS.md).

  Requiere PowerShell como administrador y el firewall activado en el perfil de red actual.

.EXAMPLE
  .\tools\prueba-sin-internet.ps1 -Simular        # solo muestra qué se bloquearía (no cambia nada)
  .\tools\prueba-sin-internet.ps1 -Exe "C:\Program Files\MiniChat LAN\MiniChat.exe"
  .\tools\prueba-sin-internet.ps1 -Estado
  .\tools\prueba-sin-internet.ps1 -Desactivar
#>
param(
    [string]$Exe = "C:\Program Files\MiniChat LAN\MiniChat.exe",
    [switch]$Desactivar,
    [switch]$Estado,
    # Solo calcula y muestra los rangos; no crea reglas ni necesita administrador.
    [switch]$Simular
)

$ErrorActionPreference = "Stop"
$group = "MiniChat LAN - prueba sin Internet"

function Show-State {
    $rules = Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue
    if (-not $rules) { Write-Host "Prueba sin Internet: DESACTIVADA (MiniChat tiene el mismo acceso que siempre)."; return }
    Write-Host "Prueba sin Internet: ACTIVA para:" -ForegroundColor Yellow
    $rules | ForEach-Object { ($_ | Get-NetFirewallApplicationFilter).Program } | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
    Write-Host "Direcciones permitidas (red local): $((Get-Content -Raw -ErrorAction SilentlyContinue (Join-Path $env:TEMP 'minichat-prueba-sin-internet.txt')))"
}

if ($Estado) { Show-State; return }

if ($Desactivar) {
    Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "Reglas de prueba eliminadas. MiniChat vuelve a la configuración normal." -ForegroundColor Green
    return
}

if (-not $Simular) {
    if (-not (Test-Path $Exe)) { throw "No existe $Exe. Indique la ruta de MiniChat.exe con -Exe." }
    $program = (Resolve-Path $Exe).Path
}

$active = Get-NetConnectionProfile | Select-Object -ExpandProperty NetworkCategory -Unique
foreach ($p in Get-NetFirewallProfile) {
    if ($p.Enabled -eq $false -and ($active -contains $p.Name -or ($p.Name -eq 'Domain' -and $active -contains 'DomainAuthenticated'))) {
        Write-Warning "El firewall está desactivado en el perfil $($p.Name): las reglas no tendrán efecto mientras siga así."
    }
}

# --- Rangos permitidos: subredes locales + loopback + multicast ---
function ToUInt([string]$ip) { $b = ([System.Net.IPAddress]::Parse($ip)).GetAddressBytes(); [array]::Reverse($b); [uint64][BitConverter]::ToUInt32($b, 0) }
function ToIp([uint64]$v) { $b = [BitConverter]::GetBytes([uint32]$v); [array]::Reverse($b); ([System.Net.IPAddress]::new($b)).ToString() }

$allowed = New-Object System.Collections.Generic.List[object]
$localText = @()
foreach ($a in Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' }) {
    $size = [uint64][math]::Pow(2, 32 - $a.PrefixLength)          # direcciones en la subred
    $net = [uint64]([math]::Floor((ToUInt $a.IPAddress) / $size) * $size)
    $last = $net + $size - 1
    $allowed.Add([pscustomobject]@{ Start = $net; End = $last })
    $localText += "$(ToIp $net)/$($a.PrefixLength) ($($a.InterfaceAlias))"
}
if ($allowed.Count -eq 0) { throw "Este equipo no tiene ninguna IPv4 de red local." }
$allowed.Add([pscustomobject]@{ Start = (ToUInt '127.0.0.0'); End = (ToUInt '127.255.255.255') })
$allowed.Add([pscustomobject]@{ Start = (ToUInt '224.0.0.0'); End = (ToUInt '239.255.255.255') })

# --- Complemento: todo lo que NO es red local se bloquea ---
$blocked = @()
[uint64]$cursor = 0
foreach ($r in ($allowed | Sort-Object Start)) {
    if ($r.Start -gt $cursor) { $blocked += "$(ToIp $cursor)-$(ToIp ($r.Start - 1))" }
    if ($r.End + 1 -gt $cursor) { $cursor = $r.End + 1 }
}
if ($cursor -le [uint64]4294967295) { $blocked += "$(ToIp $cursor)-255.255.255.255" }
$blocked += "::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"

if ($Simular) {
    Write-Host "Red local permitida: $($localText -join ', ') + 127.0.0.0/8 + 224.0.0.0/4"
    Write-Host "Se bloquearían para MiniChat.exe estos rangos:"
    $blocked | ForEach-Object { Write-Host "  $_" }
    return
}

Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
foreach ($dir in 'Outbound', 'Inbound') {
    New-NetFirewallRule -Group $group -DisplayName "MiniChat sin Internet ($dir)" -Direction $dir -Action Block `
        -Program $program -RemoteAddress $blocked -Profile Any | Out-Null
}
($localText -join ', ') | Set-Content (Join-Path $env:TEMP 'minichat-prueba-sin-internet.txt')

Write-Host "Prueba sin Internet ACTIVADA para $program" -ForegroundColor Yellow
Write-Host "MiniChat solo puede comunicarse con: $($localText -join ', ') (y multicast local)."
Write-Host "El resto de programas siguen con Internet. Pruebe ahora descubrir, emparejar y enviar mensajes y stickers."
Write-Host "En MiniChat: Ajustes › Redes › Prueba sin Internet mostrará que las reglas están activas."
Write-Host "Para deshacerlo: .\tools\prueba-sin-internet.ps1 -Desactivar"
