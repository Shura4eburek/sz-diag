# Показать картинку на экране клиента (доставка — szcli push).
# Грабля (123123): Start-Process <файл>.png падает InvalidOperationException, если
# ассоциация ведёт на UWP-«Фотографии», а пакет удалён — на битой машине это норма.
# Поэтому открываем браузером (он win32 и жив), Photo Viewer — запасной путь.
# Запуск: szcli push <СЗ> <папка-с-картинкой>
#         szcli exec <СЗ> -f tools\recipes\client\show-image.ps1
param([string]$Path = 'C:\Windows\Temp\meme.png')
if (-not (Test-Path $Path)) { throw ('нет файла: ' + $Path) }
$browsers = @(
    'C:\Program Files\Google\Chrome\Application\chrome.exe',
    'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe',
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
)
$exe = $browsers | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($exe) {
    Start-Process -FilePath $exe -ArgumentList ('--new-window', ('file:///' + $Path.Replace('\', '/')))
    ('открыто в: ' + $exe)
} else {
    Start-Process 'rundll32.exe' -ArgumentList ('shimgvw.dll,ImageView_Fullscreen', $Path)
    'открыто в Windows Photo Viewer'
}
