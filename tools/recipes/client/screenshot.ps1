# Снимок экрана клиента.
# Грабля (123123): у машины после ремонта не осталось UWP-приложений, а проверить
# «что реально на экране» было нечем — GUI по SSH не посмотришь, скриншот руками
# просить у клиента долго. Снимок + `szcli pull` заменяют «покажи, что видишь».
# Запуск: szcli exec <СЗ> -f tools\recipes\client\screenshot.ps1
#         szcli pull <СЗ> "C:\Windows\Temp\szdiag-screen.png"
# Важно: агент должен работать в интерактивной сессии пользователя — под SYSTEM
# (session 0) CopyFromScreen вернёт чёрный кадр.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$gfx.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$path = 'C:\Windows\Temp\szdiag-screen.png'
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$gfx.Dispose()
$bmp.Dispose()
('saved: ' + $path + '  ' + [math]::Round((Get-Item $path).Length / 1KB, 1) + ' KB')
