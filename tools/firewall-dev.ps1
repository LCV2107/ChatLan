<#
.SYNOPSIS
  Reglas de firewall LIMITADAS para probar un MiniChat.exe sin instalar (por ejemplo, en la prueba entre dos PCs).
  Equivalen a las que crea el MSI: solo ese ejecutable, entrada TCP 47821 y UDP 47820, perfiles Privado y
  Dominio, ámbito subred local. No desactiva el firewall ni toca la red Pública.

.EXAMPLE
  # PowerShell como administrador, desde la raíz del repositorio:
  .\tools\firewall-dev.ps1 -Exe .\src\MiniChat.App\bin\Release\net10.0-windows\win-x64\MiniChat.exe
  .\tools\firewall-dev.ps1 -Remove
#>
param(
    [string]$Exe,
    [switch]$Remove
)

$group = "MiniChat LAN (desarrollo)"
if ($Remove) {
    Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "Reglas del grupo '$group' eliminadas."
    return
}
if (-not $Exe -or -not (Test-Path $Exe)) { throw "Indique la ruta de MiniChat.exe con -Exe." }
$program = (Resolve-Path $Exe).Path

New-NetFirewallRule -Group $group -DisplayName "MiniChat LAN dev (TCP 47821)" -Direction Inbound -Action Allow `
    -Program $program -Protocol TCP -LocalPort 47821 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Group $group -DisplayName "MiniChat LAN dev (UDP 47820)" -Direction Inbound -Action Allow `
    -Program $program -Protocol UDP -LocalPort 47820 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
Write-Host "Reglas creadas para $program (perfiles Privado y Dominio, solo subred local)."

# Windows crea reglas de BLOQUEO automáticamente cuando aparece su aviso de firewall; prevalecen sobre las de permitir.
$blocks = Get-NetFirewallApplicationFilter | Where-Object { $_.Program -ieq $program } |
    Get-NetFirewallRule | Where-Object { $_.Action -eq 'Block' -and $_.Direction -eq 'Inbound' }
if ($blocks) {
    Write-Warning "Existen reglas de bloqueo de entrada para este ejecutable (creadas por el aviso de Windows):"
    $blocks | ForEach-Object { Write-Warning ("  {0} (perfil {1})" -f $_.DisplayName, $_.Profile) }
    Write-Warning "Revíselas en 'Firewall de Windows con seguridad avanzada' si bloquean perfiles Privado o Dominio."
}
Write-Host "Si su red figura como Pública, cámbiela a Privada en Configuración > Red e Internet."
