# СЗ 162003: клиент жалуется на лаг в CS, когда мышь воткнута в красный/синий порт задней панели,
# в чёрном лагов нет. Красный/синий = USB 3.x, чёрный = USB 2.0. Классическая причина такой
# картины — приёмник беспроводной мыши (2.4 ГГц) рядом с USB 3.x: широкополосный шум USB3
# садится на 2.4 ГГц. Проверяем: чем клиент реально пользовался (донгл или провод),
# в какие порты втыкал и когда.
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB'
$rows = @()
Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
  $vidpid = $_.PSChildName
  Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
    $key = $_
    $p = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
    if (-not $p) { return }
    $desc = $p.DeviceDesc
    if ($desc) { $desc = ($desc -split ';')[-1] }
    $props = Join-Path $key.PSPath 'Properties'
    $installed = $null
    $lastArrival = $null
    $lastRemoval = $null
    if (Test-Path $props) {
      $map = @{
        '{83da6326-97a6-4088-9453-a1923f573b29}\0064' = 'installed'
        '{83da6326-97a6-4088-9453-a1923f573b29}\0066' = 'arrival'
        '{83da6326-97a6-4088-9453-a1923f573b29}\0067' = 'removal'
      }
      foreach ($k in $map.Keys) {
        $vp = Join-Path $props $k
        if (Test-Path $vp) {
          $v = (Get-ItemProperty $vp -ErrorAction SilentlyContinue).'(default)'
          if ($v -is [byte[]] -and $v.Length -eq 8) {
            $ft = [BitConverter]::ToInt64($v, 0)
            try { $dt = [DateTime]::FromFileTime($ft) } catch { $dt = $null }
            switch ($map[$k]) {
              'installed' { $installed = $dt }
              'arrival'   { $lastArrival = $dt }
              'removal'   { $lastRemoval = $dt }
            }
          }
        }
      }
    }
    $rows += [pscustomobject]@{
      VidPid   = $vidpid
      Desc     = $desc
      Serial   = $key.PSChildName
      Loc      = $p.LocationInformation
      Service  = $p.Service
      Installed= $installed
      Arrival  = $lastArrival
      Removal  = $lastRemoval
    }
  }
}

Write-Output '=== Все USB-устройства из реестра (по времени последнего подключения) ==='
$rows | Sort-Object { if ($_.Arrival) { $_.Arrival } else { [DateTime]::MinValue } } -Descending |
  ForEach-Object {
    Write-Output ("{0,-22} {1}" -f $_.VidPid, $_.Desc)
    Write-Output ("    serial={0}  loc={1}  service={2}" -f $_.Serial, $_.Loc, $_.Service)
    Write-Output ("    installed={0}  last-arrival={1}  last-removal={2}" -f $_.Installed, $_.Arrival, $_.Removal)
  }
