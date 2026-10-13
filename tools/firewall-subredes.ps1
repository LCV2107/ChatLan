<#
.SYNOPSIS
  Permite que MiniChat LAN se comunique con otras subredes de la oficina (por ejemplo, otras áreas o pisos
  unidos por el router). Ejecutar COMO ADMINISTRADOR en cada equipo que deba recibir mensajes de esas subredes.

  Hace dos cosas:
   1. Crea reglas de entrada del firewall de Windows limitadas: solo MiniChat.exe, solo TCP 47821 y UDP 47820,
      solo perfiles Privado y Dominio, y solo desde las subredes indicadas.
   2. Guarda las subredes como directiva del equipo (HKLM\SOFTWARE\MiniChatLAN, valor SubredesRemotas):
      MiniChat las usa para todos los usuarios de este equipo y no se pueden quitar desde la aplicación.
      Con -SinDirectiva solo se crean las reglas de firewall.

  No desactiva el firewall, no toca la red Pública ni abre otros puertos o programas.
  Las reglas de la subred propia (las del MSI) no se modifican.

.EXAMPLE
  .\tools\firewall-subredes.ps1 -Subredes 10.10.161.0/24,10.10.162.0/24
  .\tools\firewall-subredes.ps1 -Subredes 10.10.161.0/24 -Exe "D:\MiniChat\MiniChat.exe"
  .\tools\firewall-subredes.ps1 -Estado
  .\tools\firewall-subredes.ps1 -Quitar
#>
param(
    [string[]]$Subredes,
    [string]$Exe = (Join-Path $env:ProgramFiles 'MiniChat LAN\MiniChat.exe'),
    [switch]$SinDirectiva,
    [switch]$Estado,
    [switch]$Quitar
)

$ErrorActionPreference = 'Stop'
$group = 'MiniChat LAN (otras subredes)'
$regKey = 'HKLM:\SOFTWARE\MiniChatLAN'
$regValue = 'SubredesRemotas'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-Estado {
    $rules = Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue
    if (-not $rules) { Write-Host 'No hay reglas de MiniChat para otras subredes.' }
    foreach ($r in $rules) {
        $addr = ($r | Get-NetFirewallAddressFilter).RemoteAddress -join ', '
        $port = $r | Get-NetFirewallPortFilter
        Write-Host ("  {0}: {1} {2}, perfiles {3}, desde {4}" -f $r.DisplayName, $port.Protocol, $port.LocalPort, $r.Profile, $addr)
    }
    $current = (Get-ItemProperty -Path $regKey -Name $regValue -ErrorAction SilentlyContinue).$regValue
    if ($current) { Write-Host "Directiva del equipo (todas las cuentas): $current" }
    else { Write-Host 'Sin directiva del equipo: cada usuario añade las subredes en Ajustes.' }
}

if ($Estado) { Show-Estado; return }
if (-not (Test-Admin)) { throw 'Ejecute PowerShell como administrador.' }

if ($Quitar) {
    Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Test-Path $regKey) { Remove-ItemProperty -Path $regKey -Name $regValue -ErrorAction SilentlyContinue }
    Write-Host "Reglas '$group' y directiva de subredes eliminadas. La subred propia sigue funcionando."
    return
}

if (-not $Subredes -or $Subredes.Count -eq 0) { throw 'Indique las subredes con -Subredes, por ejemplo: -Subredes 10.10.161.0/24,10.10.162.0/24' }

# Validación: IPv4 privada (10/8, 172.16/12, 192.168/16) en notación CIDR, de /16 a /30; se normaliza a la dirección de red.
$normalized = @()
foreach ($item in ($Subredes -join ',').Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
    $text = $item.Trim()
    $parts = $text.Split('/')
    $ip = $null
    if ($parts.Count -ne 2 -or -not [Net.IPAddress]::TryParse($parts[0], [ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork') {
        throw "'$text' no es una subred válida. Use el formato 10.10.161.0/24."
    }
    $prefix = 0
    if (-not [int]::TryParse($parts[1], [ref]$prefix) -or $prefix -lt 16 -or $prefix -gt 30) {
        throw "'$text': el prefijo debe estar entre /16 y /30."
    }
    $b = $ip.GetAddressBytes()
    $value = ([uint32]$b[0] -shl 24) -bor ([uint32]$b[1] -shl 16) -bor ([uint32]$b[2] -shl 8) -bor [uint32]$b[3]
    $mask = [uint32](([uint64]4294967295 -shl (32 - $prefix)) -band [uint64]4294967295)
    $net = $value -band $mask
    $o1 = ($net -shr 24) -band 255; $o2 = ($net -shr 16) -band 255
    $private = ($o1 -eq 10) -or ($o1 -eq 172 -and $o2 -ge 16 -and $o2 -le 31) -or ($o1 -eq 192 -and $o2 -eq 168)
    if (-not $private) { throw "'$text' no es un rango privado (10.x, 172.16-31.x, 192.168.x). MiniChat no se comunica por Internet." }
    $normalized += ('{0}.{1}.{2}.{3}/{4}' -f $o1, $o2, (($net -shr 8) -band 255), ($net -band 255), $prefix)
}
$normalized = $normalized | Select-Object -Unique

if (-not (Test-Path $Exe)) { throw "No se encontró MiniChat.exe en '$Exe'. Indique la ruta con -Exe." }
$program = (Resolve-Path $Exe).Path

# Se recrean las reglas del grupo para que reflejen exactamente la lista indicada.
Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Group $group -DisplayName 'MiniChat LAN (TCP 47821, otras subredes)' -Direction Inbound -Action Allow `
    -Program $program -Protocol TCP -LocalPort 47821 -Profile Private,Domain -RemoteAddress $normalized | Out-Null
New-NetFirewallRule -Group $group -DisplayName 'MiniChat LAN (UDP 47820, otras subredes)' -Direction Inbound -Action Allow `
    -Program $program -Protocol UDP -LocalPort 47820 -Profile Private,Domain -RemoteAddress $normalized | Out-Null
Write-Host "Firewall: MiniChat acepta conexiones desde $($normalized -join ', ') (perfiles Privado y Dominio)."

if (-not $SinDirectiva) {
    if (-not (Test-Path $regKey)) { New-Item -Path $regKey | Out-Null }
    Set-ItemProperty -Path $regKey -Name $regValue -Value ($normalized -join ',') -Type String
    Write-Host 'Directiva guardada: MiniChat usará estas subredes para todos los usuarios de este equipo (al reiniciar MiniChat).'
}

$profiles = Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' }
foreach ($p in $profiles) {
    Write-Warning ("La red '{0}' ({1}) es Pública: estas reglas no se aplican en ella. Márquela como Privada si es la red de trabajo." -f $p.Name, $p.InterfaceAlias)
}
Write-Host 'Recuerde: los equipos de esas subredes necesitan lo mismo con la subred de este equipo.'
