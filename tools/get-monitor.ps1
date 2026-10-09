# Query monitor manufacturer / model via WMI (WmiMonitorID).
$nsp = 'root\wmi'
$idl = Get-CimInstance -Namespace $nsp -ClassName WmiMonitorID -ErrorAction SilentlyContinue
if (-not $idl) { $idl = Get-WmiObject WmiMonitorID -Namespace $nsp -ErrorAction SilentlyContinue }

function Bytes-ToStr($bytes) {
  if ($null -eq $bytes) { return '' }
  ($bytes | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) -join ''
}

foreach ($id in $idl) {
  $mfr = Bytes-ToStr $id.ManufacturerName
  $prod = Bytes-ToStr $id.ProductCodeID
  $name = Bytes-ToStr $id.UserFriendlyName
  $sn = Bytes-ToStr $id.SerialNumberID
  Write-Output ("Manufacturer=" + $mfr)
  Write-Output ("ProductCode=" + $prod)
  Write-Output ("FriendlyName=" + $name)
  Write-Output ("Serial=" + $sn)
  Write-Output "---"
}

# Fallback: registry EDID PnP id
$disps = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\DISPLAY' -ErrorAction SilentlyContinue
foreach ($d in $disps) {
  foreach ($u in (Get-ChildItem $d.PSPath -ErrorAction SilentlyContinue)) {
    $p = Join-Path $u.PSPath 'Device Parameters'
    $edid = (Get-ItemProperty -Path $p -Name EDID -ErrorAction SilentlyContinue).EDID
    if ($edid) {
      # PnP id: bytes 8-9 little-endian 5-bit letters
      $w1 = $edid[8]; $w2 = $edid[9]
      $letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
      $vals = @(
        (($w1 -shr 2) -band 0x1F), ((($w1 -band 3) -shl 3) -bor (($w2 -shr 5) -band 7)), ($w2 -band 0x1F)
      )
      $pnp = ($vals | ForEach-Object { $letters[$_-1] }) -join ''
      Write-Output ("Registry: " + $d.PSChildName + " pnp=" + $pnp)
    }
  }
}
