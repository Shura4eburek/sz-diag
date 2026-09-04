namespace SzDiag.Contracts;

/// <summary>Транзиентный («качели нагрузка/простой») стресс-тест — промотано из рецепта
/// <c>tools/recipes/client/stress-transient.ps1</c> в CLI (бэклог п.154, СЗ 161716).
///
/// Боль: ровный (плато) стресс на этой заявке прошёл ЧИСТО (75 минут y-cruncher, 5 итераций,
/// ни одной ошибки), а машина у клиента вырубалась пачками. Разбор игровых сессий показал
/// почему — ни один hard-off не пришёлся на саму нагрузку: все случились на ПЕРЕХОДЕ (снятие
/// нагрузки → простой) или уже на простаивающей машине. Ровный тест таких переходов не
/// создаёт вообще — он поднял нагрузку один раз и держит, поэтому и «проходит» на дефектной
/// машине. Тот же почерк — на 160587 (4 hard-off на простое, 48-минутный prime95 тоже чист).
///
/// Цикл здесь — CPU+RAM (y-cruncher) и, опционально, GPU (FurMark) вместе: подняли, держим
/// <c>OnSeconds</c>, оборвали, простой <c>OffSeconds</c>, и так до <c>TotalHours</c>. Резкий
/// обрыв — не грубость, а суть теста: именно на нём машина и падает. Метка в лог пишется
/// ПОСЛЕ каждого снятия нагрузки — упавшая машина оставляет последнюю строку, по которой
/// видно, в каком именно цикле и в какой фазе случился отказ.</summary>
public static class TransientStressScript
{
    /// <summary>Лог цикла на клиенте — тот же каталог, что и у прочих ad-hoc прогонов
    /// (`RecipeWorkDirs` в <see cref="ClientTraces"/>), чтобы `client cleanup`/`stress stop`
    /// убирали его без отдельного знания об этом тесте.</summary>
    public const string LogPath = @"C:\OCCT\transient.log";

    /// <summary>Скрипт транзиентного цикла. Инструменты ищутся рядом с агентом и в облачном
    /// фоллбэке — тем же способом, что и в исходном рецепте (агент не выставляет наружу свой
    /// `ToolsDirectory.Resolve`, а через `exec` доступен только PowerShell).</summary>
    public static string BuildScript(string sz, int onSeconds = 60, int offSeconds = 40,
        double totalHours = 1.5, int memGb = 8, bool withGpu = true)
    {
        var withGpuLiteral = withGpu ? "$true" : "$false";
        var totalMinutes = totalHours * 60;
        return $$"""
            $OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
            $Sz       = '{{sz}}'
            $OnSec    = {{onSeconds}}
            $OffSec   = {{offSeconds}}
            $TotalMin = {{totalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
            $MemG     = {{memGb}}
            $WithGpu  = {{withGpuLiteral}}
            $log      = '{{LogPath}}'

            $proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
            $base = if ($proc) { Split-Path $proc.ExecutablePath -Parent } else { $null }
            function Find-Tool([string]$rel) {
                @("$base\tools\$rel", "C:\ProgramData\szdiag\tools\$rel") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
            }
            $yc = Find-Tool 'ycruncher\y-cruncher.exe'
            $fm = Find-Tool 'furmark\furmark.exe'
            if (-not $yc) { "FATAL: y-cruncher ne nayden - szcli push $Sz ycruncher"; exit 1 }
            if ($WithGpu -and -not $fm) { 'FurMark ne nayden - idu bez GPU'; $WithGpu = $false }

            if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory 'C:\OCCT' | Out-Null }
            "start: {0:dd.MM HH:mm:ss}  cikl {1}s nagruzka / {2}s prostoy, vsego {3} min (GPU: {4})" -f `
                (Get-Date), $OnSec, $OffSec, $TotalMin, $WithGpu | Tee-Object -FilePath $log -Append

            $deadline = (Get-Date).AddMinutes($TotalMin)
            $n = 0
            while ((Get-Date) -lt $deadline) {
                $n++
                $ycP = Start-Process -FilePath $yc -WorkingDirectory (Split-Path $yc -Parent) `
                    -ArgumentList 'stress', "-M:${MemG}G", "-D:$OnSec", "-TL:$($OnSec + 30)", 'VT3', 'N63' `
                    -WindowStyle Hidden -PassThru
                $fmP = $null
                if ($WithGpu) {
                    $fmP = Start-Process -FilePath $fm -WorkingDirectory (Split-Path $fm -Parent) `
                        -ArgumentList '--demo', 'furmark-gl', '--width', '1280', '--height', '720', '--max-time', $OnSec `
                        -WindowStyle Hidden -PassThru
                }

                Start-Sleep -Seconds $OnSec

                foreach ($p in @($ycP, $fmP)) {
                    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
                }
                Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like '*\ycruncher\Binaries\*' } |
                    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

                # Metka POSLE snyatiya nagruzki: esli mashina padaet zdes, poslednyaya stroka
                # loga pokazhet, na kakom cikle i v kakoy faze (161716 - vyruboni shli po
                # prostaivayushchey mashine, ne pod nagruzkoy).
                "cikl {0}: {1:HH:mm:ss} nagruzka snyata, prostoy {2}s" -f $n, (Get-Date), $OffSec |
                    Tee-Object -FilePath $log -Append
                Start-Sleep -Seconds $OffSec
            }
            "finish: {0:dd.MM HH:mm:ss}, ciklov {1}" -f (Get-Date), $n | Tee-Object -FilePath $log -Append
            """;
    }

    /// <summary>Приёмка прогона: результат ровного (плато) стресса не защитывается как
    /// отрицательный для симптома «hard-off на простое/переходе» — такой тест этот класс
    /// дефектов физически не проверяет. Дописывается в конец каждого <c>report.md</c>,
    /// который собирает <see cref="SzDiag.Agent.TestReportRunner"/> (единственный плато-путь —
    /// `szcli test run`; `stress start` без `--transient` вообще отказывается стартовать,
    /// см. <c>StressCommand</c>).</summary>
    public const string PlateauCoverageWarning =
        "ℹ Это ровный (плато) прогон — переходов нагрузка↔простой он не создаёт. Для симптома "
        + "«вырубоны/hard-off на простое» чистый результат НЕ засчитывается как отрицательный: "
        + "проверен только режим плато. Дискриминатор — szcli stress start <СЗ> --transient.";
}
