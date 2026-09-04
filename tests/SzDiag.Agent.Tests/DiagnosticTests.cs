using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class DiagnosticProbesTests
{
    [Fact]
    public void Suite_HasExpectedSections_ExceptNetworkAndSecurity()
    {
        var expected = new[]
        {
            "system", "os", "cpu", "memory", "gpu", "storage",
            "temps", "drivers", "events", "reboots", "whea", "thermal", "livekernel", "reliability", "battery"
        };
        Assert.Equal(expected, DiagnosticProbes.Sections);
        // Каталог проб и словарь для валидации в CLI обязаны совпадать: иначе szcli либо
        // отвергнет живую секцию, либо пропустит несуществующую (бэклог п.6).
        Assert.Equal(DiagSections.All, DiagnosticProbes.Sections);
        Assert.DoesNotContain("network", DiagnosticProbes.Sections);
        Assert.DoesNotContain("security", DiagnosticProbes.Sections);
    }

    [Fact]
    public void OsProbe_CoversProvenanceEvidence_NotJustInstallDate()
    {
        // Регрессия (бэклог п.162, СЗ 161346): вывод «ОС старше даты сборки, значит
        // переносилась» строился на ОДНОМ InstallDate, хотя он переживает feature update и
        // едет внутри образа. Клиент оспорил вывод и потребовал письменное подтверждение.
        var run = Body("os");

        Assert.Contains("CloneTag", run);
        Assert.Contains("GeneralizationState", run);
        Assert.Contains("InstallDate", run);
        Assert.Contains("BuildLabEx", run);
        Assert.Contains("setupapi.dev.log", run);
        Assert.Contains("Windows.old", run);
        Assert.Contains("Prizrakov", run);
        Assert.Contains("Aktivaciya", run);
    }

    [Fact]
    public void Suite_AllStepsAreCommandProbes_WithIdAndRun()
    {
        Assert.All(DiagnosticProbes.Suite.Steps, s =>
        {
            Assert.Equal("command", s.Type);
            Assert.False(string.IsNullOrWhiteSpace(s.Id));
            Assert.False(string.IsNullOrWhiteSpace(s.Run));
        });
    }

    [Fact]
    public void RebootsProbe_ReadsFullHistoryAndDecodesBugchecks()
    {
        // Регрессия (бэклог п.61/13): секция резала Kernel-Power 41 до 20 записей и печатала
        // BugcheckCode десятичным. Итог, дата установки ОС и hex-имена — обязательны.
        var run = DiagnosticProbes.Suite.Steps.Single(s => s.Id == "reboots").Run!;

        Assert.DoesNotContain("Id=41 } -MaxEvents", run);   // историю берём целиком
        Assert.Contains("TOTAL:", run);                      // общее число событий
        Assert.Contains("per-day histogram", run);           // деградация видна по дням
        Assert.Contains("OS installed", run);                // «дефект с первого дня»
        Assert.Contains("Fmt-Bug", run);                     // hex + имя стоп-кода
        Assert.Contains("'190'='ATTEMPTED_WRITE_TO_READONLY_MEMORY'", run);
    }

    [Fact]
    public void RebootsProbe_SeparatesPowerButtonFromRealHardOff()
    {
        // Регрессия (п.93): на 161312 два из пяти «аварийных выключений» оказались нажатием
        // кнопки питания (PowerButtonTimestamp != 0) — вердикт по заявке менялся вместе с ними.
        var run = DiagnosticProbes.Suite.Steps.Single(s => s.Id == "reboots").Run!;

        Assert.Contains("PowerButtonTimestamp", run);
        Assert.Contains("knopka pitaniya", run);
        Assert.Contains("hard-off (nastoyashchiy obryv pitaniya)", run);
        // «Дефект приехал с завода» считается только по настоящим обрывам.
        Assert.Contains("First hard-off: {0:N1} h after OS install", run);
        Assert.DoesNotContain("First unexpected shutdown", run);
    }

    private static string Body(string section)
        => DiagnosticProbes.Suite.Steps.Single(s => s.Id == section).Run!;

    [Fact]
    public void HistorySections_SeparateEventsFromOtherHardware()
    {
        // Регрессия (п.92): машину гоняли под переносной сервисной Windows, и diag выдал 79
        // Kernel-Power 41 и 12 MCE «на одном ядре» — историю ЧУЖИХ машин, из которой чуть не
        // выросла замена процессора.
        foreach (var section in new[] { "reboots", "whea" })
        {
            var run = Body(section);
            Assert.Contains("Write-HwWindow", run);
            Assert.Contains("Split-ByHwWindow", run);
            Assert.Contains("DRUGOGO zheleza", run);
        }
    }

    [Fact]
    public void RebootsProbe_PrintsForeignHardwareHistoryAsItsOwnBlock()
    {
        // Регрессия (бэклог п.210, СЗ 161498): события ДО границы молча отбрасывались одной
        // строкой с count — реальная картина «3 на чужом железе + 26 на этой сборке» терялась,
        // и сводка выглядела как «29 событий, история с прошлого года».
        var run = Body("reboots");

        Assert.Contains("NA ETOM ZHELEZE", run);
        Assert.Contains("DO SBORKI (CHUZHOE ZHELEZO", run);
    }

    [Fact]
    public void AllProbeBodies_ParseAsValidPowerShell()
    {
        // Страж (п.182/196): синтаксическая ошибка в пробе должна падать на сборке, а не
        // молча выпадать секцией на живой заявке. Один powershell токенизирует все пробы.
        var dir = Path.Combine(Path.GetTempPath(), $"szprobes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var step in DiagnosticProbes.Suite.Steps)
                File.WriteAllText(Path.Combine(dir, step.Id + ".ps1"), step.Run!,
                    new System.Text.UTF8Encoding(true));

            var check = $$"""
                $bad = @()
                foreach ($f in Get-ChildItem '{{dir}}' -Filter *.ps1) {
                    $errors = $null
                    [void][System.Management.Automation.PSParser]::Tokenize(
                        (Get-Content $f.FullName -Raw), [ref]$errors)
                    if ($errors.Count -gt 0) {
                        $bad += "$($f.Name): $($errors[0].Message) (строка $($errors[0].Token.StartLine))"
                    }
                }
                if ($bad.Count -gt 0) { $bad; exit 1 } else { 'all-ok' }
                """;
            var r = new PowerShellRunner().Run(check, throwOnError: false,
                timeout: TimeSpan.FromSeconds(60));

            Assert.True(r.ExitCode == 0 && r.StdOut.Contains("all-ok"),
                $"пробы с ошибками разбора:\n{r.StdOut}\n{r.StdErr}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SystemProbe_SeparatesSleepFromUptime()
    {
        // Регрессия (п.132): «Uptime 11 суток» ушёл в письмо клиенту как «работала 11 діб
        // без збоїв», а машина проспала в S3 почти всё это время. Uptime не вычитает сон,
        // и его не сбрасывают ни гибернация, ни fast startup.
        var run = Body("system");

        Assert.Contains("Kernel-Power", run);       // сумма интервалов сна 42→107
        Assert.Contains("realnaya rabota", run);
        Assert.Contains("HiberbootEnabled", run);   // fast startup виден сразу
        Assert.Contains("PowerOnHours", run);       // отсылка к наработке, а не календарю
    }

    [Fact]
    public void ThermalProbe_ExplicitlyWarnsThermtripIsNotLogged()
    {
        // Регрессия (бэклог п.36b, СЗ 160636): THERMTRIP (аппаратный термозащитный сброс) не
        // логируется в принципе — питание снимается в железе, ОС не получает шанса на запись.
        // Отсутствие событий тротлинга легко (и неверно) читается как "перегрев исключён".
        var run = Body("thermal");

        Assert.Contains("THERMTRIP", run);
        Assert.Contains("NE LOGIRUETSYA", run);
        Assert.Contains("NE ISKLYUCHAET teplovoy stsenariy", run);
    }

    [Fact]
    public void ThermalProbe_FiltersThrottlingByExplicitProviderName()
    {
        // Регрессия (бэклог п.36b, СЗ 160636): фильтр Id=37 без ProviderName поймал ЧУЖОЕ
        // событие (Microsoft-Windows-Time-Service) и дал ложный вывод "тротлинга нет".
        var run = Body("thermal");

        Assert.Contains("ProviderName='Microsoft-Windows-Kernel-Processor-Power'; Id=37,86", run);
        Assert.Contains("YAVNYY ProviderName", run);
    }

    [Fact]
    public void ThermalProbe_BucketsHardOffByTimeOfDay()
    {
        // Распределение вырубонов по времени суток — косвенный признак теплового сценария
        // (вечер/жара после часов работы в закрытом корпусе).
        var run = Body("thermal");

        Assert.Contains("Kernel-Power", run);
        Assert.Contains("vecher (18-24)", run);
        Assert.Contains("po vremeni sutok", run);
    }

    [Fact]
    public void ThermalProbe_SeparatesFromOtherHardwareHistory()
    {
        var run = Body("thermal");

        Assert.Contains("Write-HwWindow", run);
        Assert.Contains("Split-ByHwWindow", run);
        Assert.Contains("Write-TzNote", run);
        Assert.Contains("Write-JournalDepthNote", run);
    }

    [Fact]
    public void MemoryProbe_ReadsVoltageForXmpDetection()
    {
        // Регрессия (бэклог п.8): на 160467 Speed=ConfiguredClockSpeed=4800 не давал понять,
        // включён ли EXPO. ConfiguredVoltage сразу отличает JEDEC (~1100 mV) от EXPO/XMP
        // (~1350-1400 mV) без захода в BIOS.
        var run = Body("memory");

        Assert.Contains("ConfiguredVoltage", run);
        Assert.Contains("MinVoltage", run);
        Assert.Contains("MaxVoltage", run);
        Assert.Contains("JEDEC", run);
    }

    [Fact]
    public void MemoryProbe_CountsModulesAndComparesWithWindowsTotal()
    {
        // Регрессия (бэклог п.200, СЗ 161211): на ASUS TUF B850-PLUS WIFI обе планки
        // репортят DeviceLocator='DIMM 1' (различаются только BankLabel) - словарь по
        // DeviceLocator схлопывал вторую планку поверх первой, и паспорт печатал "1x32"
        // вместо "2x32". Ключ по BankLabel+DeviceLocator+SN не схлопывает, плюс ИТОГО
        // сверяется с Win32_ComputerSystem.TotalPhysicalMemory.
        var run = Body("memory");

        Assert.Contains("BankLabel", run);
        Assert.Contains("ITOGO:", run);
        Assert.Contains("TotalPhysicalMemory", run);
        Assert.Contains("planok", run);
    }

    [Fact]
    public void StorageProbe_ReadsNvmeHealthLogDirectly()
    {
        // Регрессия (п.120/142): на NVMe Get-StorageReliabilityCounter отдаёт пустые
        // PowerOnHours/ошибки, а Unsafe Shutdowns — главное доказательство в претензии —
        // добывался только рецептом. Лог 02h читается напрямую через
        // IOCTL_STORAGE_QUERY_PROPERTY, без smartctl.
        var run = Body("storage");

        Assert.Contains("UnsafeShutdowns", run);
        Assert.Contains("MediaErrors", run);
        Assert.Contains("PowerOnHours", run);
        Assert.Contains("StorageDeviceProtocolSpecificProperty", run);
        // На пустых счётчиках вердикт не имеет права быть «OK».
        Assert.Contains("dannyh net", run);
    }

    [Fact]
    public void StorageProbe_MapsDeviceHarddiskToPhysicalDisk()
    {
        // Регрессия (п.122): `\Device\Harddisk1\DR1 has a bad block` не привязан к диску —
        // при двух NVMe одного вендора непонятно, клиентский сыплется или из заказа.
        var run = Body("storage");

        Assert.Contains("Win32_DiskDrive", run);
        Assert.Contains("Harddisk", run);
        Assert.Contains("SCSIPort", run);
    }

    [Fact]
    public void DriversProbe_FiltersToPresentDevicesAndCollapsesGhosts()
    {
        // Регрессия (бэклог п.167, СЗ 161190): без -PresentOnly секция печатала 300+ строк
        // устройств ДРУГИХ сборок (9800X3D/7800X3D/7500F и т.п.), на которых гонялся тот же
        // переносной образ сервиса — вывод читался как "на машине куча проблемных устройств".
        var run = Body("drivers");

        Assert.Contains("-PresentOnly", run);
        Assert.Contains("prizrakov proshlogo zheleza", run);
        // Пустой Status - это "нет данных", а не "Unknown"/проблема.
        Assert.Contains("$_.Status -and $_.Status -ne 'OK'", run);
    }

    [Fact]
    public void ReliabilityProbe_CrashDumpKindLookup_UsesIntKey()
    {
        // Регрессия (п.110): ключи хеш-таблицы — int, а лукап шёл строкой
        // `$kind["$($cc.CrashDumpEnabled)"]` — любое значение печаталось как «unknown»,
        // включая штатное 3 (small/minidump).
        var run = Body("reliability");

        Assert.DoesNotContain("$kind[\"$($cc.CrashDumpEnabled)\"]", run);
        Assert.Contains("$kind[[int]$cc.CrashDumpEnabled]", run);
    }

    [Fact]
    public void HardwareWindow_Prologue_IsAsciiAndFailsSafe()
    {
        var ps = HardwareWindow.PowerShellPrologue();

        Assert.All(ps, c => Assert.True(c < 128, $"не-ASCII в прологе окна железа: {c}"));
        // Регрессия (бэклог п.210, СЗ 161498): DEVPKEY_Device_InstallDate меняется при
        // переустановке драйвера, FirstInstallDate — момент, когда ЭТА система впервые
        // увидела ИМЕННО ЭТОТ экземпляр устройства, и не едет вместе с переустановками.
        Assert.Contains("DEVPKEY_Device_FirstInstallDate", ps);
        // Без надёжного признака ничего не отсекаем и говорим об этом прямо: выдуманная
        // граница хуже, чем её отсутствие.
        Assert.Contains("return $null", ps);
        Assert.Contains("schitat vsyu istoriyu svoey NELZYA", ps);
    }

    [Fact]
    public void HardwareWindow_UsesOnlyKeyNonRemovableDevices_NotAllPciDevices()
    {
        // Регрессия (бэклог п.210, СЗ 161498): мода по дню среди ВСЕХ PCI-устройств дала
        // границу на ГОД раньше реальной. Правильные свидетели — несъёмные ключевые
        // устройства платформы: сетевые контроллеры/шины, GPU, системный диск.
        var ps = HardwareWindow.PowerShellPrologue();

        Assert.Contains("'Net'", ps);
        Assert.Contains("'Display'", ps);
        Assert.Contains("BusType", ps);   // диск резолвится через шину, чтобы отсеять USB
        // Съёмное (USB-флешки) не должно попадать в выборку "рождения" железа.
        Assert.Contains("USB", ps);
    }

    [Fact]
    public void HardwareWindow_PrintsBothDatesAndContributingDevices()
    {
        // Расхождение "ОС старше железа" обязано быть видно прямо в шапке секции, а не
        // прятаться в одной строке — иначе его снова легко не заметить.
        var ps = HardwareWindow.PowerShellPrologue();

        Assert.Contains("SZ_OS_INSTALL", ps);
        Assert.Contains("SZ_HW_SINCE", ps);
        Assert.Contains("SZ_HW_DEVICES", ps);
    }

    [Fact]
    public void WheaProbe_AggregatesByApicBankAndDate()
    {
        // Регрессия (п.44): 276 событий печатались 40 строками без APIC ID и без общего
        // числа — локализация дефекта на одном ядре была невидима.
        var run = Body("whea");

        Assert.DoesNotContain("-MaxEvents 40", run);
        Assert.Contains("TOTAL:", run);
        Assert.Contains("by APIC ID", run);
        Assert.Contains("odnom fizicheskom yadre", run);   // маркер «все ошибки на одном ядре»
        Assert.Contains("by MCA bank", run);
        Assert.Contains("by date", run);
    }

    [Fact]
    public void WheaProbe_DecodesMcaFieldsAndFlags()
    {
        // Регрессия (п.18): MciStat/ErrorType приходилось доставать отдельным exec из XML.
        var run = Body("whea");

        Assert.Contains("MciStat", run);
        Assert.Contains("Cache Hierarchy Error", run);      // ErrorType=9, а не 8 (см. п.18)
        Assert.Contains("'10'='Bus/Interconnect Error'", run);
        Assert.Contains("PCC", run);
        Assert.Contains("UC(neispravimaya)", run);
    }

    [Fact]
    public void WheaProbe_FallsBackToBinaryCperWhenNamedFieldsAreEmpty()
    {
        // Регрессия (п.68): у Id=1 именованных полей нет вовсе, и «by Error Type / by MCA bank /
        // by APIC ID» выходили пустыми на машине с девятью фатальными ошибками.
        var run = Body("whea");

        Assert.Contains("Parse-Cper", run);
        Assert.Contains("Get-CperBytes", run);
        Assert.Contains("by CPER", run);
        Assert.Contains("Imenovannyh poley ErrorType net", run);   // пустота объяснена, а не молчит
        Assert.Contains("kanal", run);                              // MCE / PCIe / CMC
    }

    [Fact]
    public void LiveKernelProbe_LooksWhereTdrDumpsActuallyLand()
    {
        // Регрессия (п.37): по заявке «вылетает игра» отчёт говорил «всё чисто», а рядом
        // лежали 14 WATCHDOG-дампов и LiveKernelEvent 0x141.
        var run = Body("livekernel");

        Assert.Contains(@"C:\Windows\LiveKernelReports", run);
        Assert.Contains("LiveKernelEvent", run);
        Assert.Contains("VIDEO_ENGINE_TIMEOUT_DETECTED", run);   // P1=141
        Assert.Contains("VIDEO_TDR_TIMEOUT_DETECTED", run);      // P1=117
        Assert.Contains("SOVPADAET s LiveKernelEvent", run);     // сшивка с крашем приложения
    }

    [Fact]
    public void LiveKernelProbe_CountsUniqueReportsNotRawEvents()
    {
        // Регрессия (бэклог п.199, СЗ 161211): «8572 события, пачка 0x141 x30 сегодня» ушло в
        // kb как «TDR-ы воспроизводятся прямо сейчас» - неправда: WER бесконечно ретраит
        // очередь ReportQueue, 8149 событий оказались 52 уникальными отчётами (~20 инцидентов),
        // последний реальный дамп - месяц назад.
        var run = Body("livekernel");

        Assert.Contains("UNIKALNYE OTCHETY", run);
        Assert.Contains("incidentov (", run);
        Assert.Contains("retrai WER", run);
        Assert.Contains("ReportQueue", run);
        Assert.Contains("POSLEDNIJ REALNYJ INCIDENT", run);
    }

    [Fact]
    public void LiveKernelProbe_GroupsByReportIdGuid_RegardlessOfWindowsLocale()
    {
        // "Идентификатор отчета" на RU-Windows / "Report Id" на EN-Windows - оба локализованы
        // по-разному, а GUID-подпись отчёта - нет. Ловим её паттерном, а не заголовком.
        var run = Body("livekernel");

        Assert.Contains(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", run);
    }

    [Fact]
    public void LiveKernelProbe_DecodesP1AsHexThroughSharedTable()
    {
        // Регрессия (п.69): 148 событий печатались как `P1=124`, хотя это
        // 0x124 WHEA_UNCORRECTABLE_ERROR — перевод уже был сделан, но только для reboots.
        var run = Body("livekernel");

        Assert.Contains("Fmt-Bug", run);                          // общая таблица, а не своя копия
        Assert.Contains("Fmt-P1", run);
        Assert.Contains("NumberStyles]::HexNumber", run);         // P1 читается как hex
        Assert.Contains("'292'='WHEA_UNCORRECTABLE_ERROR'", run); // 0x124 в прологе
        Assert.Contains("by code", run);                          // свод по кодам с периодом
    }

    [Fact]
    public void LiveKernelProbe_SeparatesRealEventsFromBurstArtifacts()
    {
        // Регрессия (п.94): «298 событий, сыпется каждый день» оказалось пачками по 8 в одну
        // секунду — следами закрытия 3D-окна после стресс-теста, на исправном железе.
        var run = Body("livekernel");

        Assert.Contains("pachka", run);
        Assert.Contains("artefakt zakrytiya 3D-prilozheniya", run);
        Assert.Contains("ITOGO: sobytiy s dampom", run);
        Assert.Contains("ryadom damp", run);   // дискриминатор — свежий файл дампа
    }

    [Fact]
    public void LiveKernelProbe_MarksEventsNearBootOrLogonAsOwnActivityNotSymptom()
    {
        // Регрессия (бэклог п.219, СЗ 161190): пары 0x117+0x1cc легли ровно на минуту нашего
        // же замера gpu-idle-state.ps1, а также на загрузку и вход в сессию - обращение к
        // драйверу само порождает событие. Такие совпадения не должны предлагаться как симптом.
        var run = Body("livekernel");

        Assert.Contains("logon", run);
        Assert.Contains("NE simptom", run);
        Assert.Contains("zagruzkoy sistemy", run);
    }

    [Fact]
    public void ReliabilityProbe_SaysNoMinidumpsIsNotNoKernelCrashes()
    {
        var run = Body("reliability");

        Assert.Contains("!= 'net sboev yadra'", run);
        Assert.Contains("LiveKernelReports", run);
        Assert.Contains("volmgr", run);   // дампы могут не писаться в принципе (п.14)
    }

    [Fact]
    public void ReliabilityProbe_ReportsCrashControlAndSurvivesMissingSources()
    {
        // Регрессия (п.74): секция валилась целиком с `ошибка: код 1:` без подробностей, а
        // без CrashDumpEnabled ответ «дампов нет» неинтерпретируем — не пишутся вовсе или
        // BSOD не было.
        var run = Body("reliability");

        Assert.Contains("CrashDumpEnabled", run);
        Assert.Contains("AutoReboot", run);
        Assert.Contains("MEMORY.DMP", run);
        Assert.Contains("Win32_ReliabilityRecords -ErrorAction Stop", run);   // с обработкой, а не молча
        Assert.Contains("exit 0", run);                                       // «дампов нет» — не ошибка
    }

    [Fact]
    public void StorageProbe_SummarizesJournalDiskEventsByDeviceAndFlagsRemovable()
    {
        // Регрессия (бэклог п.141, СЗ 160705): 396 событий `disk Id=51` чуть не уехали в акт
        // клиенту как "ошибок накопителя нет" — ни одна секция их не агрегировала и не
        // привязывала к устройству. Все 396 оказались за один день на USB-флешке
        // (Harddisk1, съёмный), 0 из 396 рядом с вырубоном.
        var run = Body("storage");

        Assert.Contains("Svodka diskovyh sobytiy po Harddisk N", run);
        Assert.Contains("SEMNYY NOSITEL", run);
        Assert.Contains("Kernel-Power 41", run);
        Assert.Contains("InterfaceType -eq 'USB'", run);
    }

    [Fact]
    public void StorageProbe_ResolvesDiskAtEventTime_NotTodaysMap()
    {
        // Регрессия (бэклог п.133, СЗ 161346): диски физически поменяли местами 29.07 между
        // 12:11 и 13:57 - карта "на сейчас" (Win32_DiskDrive) дала ЗЕРКАЛЬНУЮ привязку старых
        // ошибок: указала на диск из заказа вместо клиентского, хотя все ошибки были на
        // клиентском ДО перестановки. Резолв обязан идти по истории Partition/Diagnostic 1006
        // на момент КАЖДОГО события, а не по текущему состоянию.
        var run = Body("storage");

        Assert.Contains("Get-DiskNumberHistory", run);
        Assert.Contains("Resolve-DiskAtTime", run);
        Assert.Contains("NA MOMENT SOBYTIYA", run);
        // Молчаливая подстановка сегодняшней модели на архивную ошибку хуже, чем её
        // отсутствие: вердикт строится на ней.
        Assert.Contains("model NEIZVESTEN na tu datu", run);
    }

    [Fact]
    public void StorageProbe_ReportsDiskSlotSwapsSeparately()
    {
        // Критерий готовности п.70/133: перестановки дисков за окно перечислены отдельным
        // блоком - "диски поменялись местами" маскирует дефект слота и рвёт статистику.
        var run = Body("storage");

        Assert.Contains("Write-DiskSlotSwaps", run);
        Assert.Contains("Smena nomerov diskov", run);
    }

    [Fact]
    public void DiskNumberHistory_Prologue_IsAsciiAndReadsPartitionDiagnostic1006()
    {
        var ps = DiskNumberHistory.PowerShellPrologue();

        Assert.All(ps, c => Assert.True(c < 128, $"не-ASCII в прологе истории дисков: {c}"));
        Assert.Contains("Microsoft-Windows-Partition/Diagnostic", ps);
        Assert.Contains("Id=1006", ps);
        Assert.Contains("DiskNumber", ps);
    }

    [Fact]
    public void StorageProbe_MapsPagefileToPhysicalDiskAndSplitsUncorrectable()
    {
        // Регрессия (п.27): «ReadErrors: 393» выглядело как шум, хотя все 393 неисправимы,
        // а связь «pagefile на умирающем HDD → 0x154» не собиралась вовсе.
        var run = Body("storage");

        Assert.Contains("Win32_PageFileUsage", run);
        Assert.Contains("ReadErrorsUncorrected", run);
        Assert.Contains("WriteErrorsUncorrected", run);
        Assert.Contains("VERDICT", run);
        Assert.Contains("SUSPECT", run);
        Assert.Contains("Bukva -> fizicheskiy disk", run);
        Assert.Contains("UNEXPECTED_STORE_EXCEPTION", run);
        Assert.Contains("NE 'diski zdorovy'", run);   // пустые счётчики ≠ здоровые диски
    }

    [Fact]
    public void StorageProbe_ShowsDisksHiddenInStoragePools()
    {
        // Регрессия (бэклог п.239, СЗ 111111): HDD 1 ТБ в пустом пуле Storage Spaces виден в
        // диспетчере устройств, но отсутствует в "Управлении дисками"/diskpart и в карте
        // HarddiskN - выглядит как пропавший диск, хотя физически исправен. Get-PhysicalDisk -
        // единственное место, где CanPool/CannotPoolReason это объясняют.
        var run = Body("storage");

        Assert.Contains("CanPool", run);
        Assert.Contains("CannotPoolReason", run);
        Assert.Contains("Get-StoragePool", run);
        Assert.Contains("V POOLE Storage Spaces", run);
        Assert.Contains("POOL PUSTOY", run);
    }

    [Fact]
    public void StorageProbe_ReportsOfflineAndReadOnlyDisks()
    {
        // Та же симптоматика "диск есть, а разметить нельзя" даёт Offline/ReadOnly/SAN policy.
        var run = Body("storage");

        Assert.Contains("IsOffline", run);
        Assert.Contains("IsReadOnly", run);
    }

    [Fact]
    public void EventsProbe_CountsPerIdAndReadsRareIdsWithoutLimit()
    {
        // Регрессия (п.31): общий MaxEvents на смеси шумных и редких Id съел Kernel-Power 41.
        var run = Body("events");

        Assert.Contains("Schetchiki po Id", run);
        Assert.Contains("Redkie kritichnye Id - BEZ limita", run);
        Assert.Contains("Kernel-Power 41", run);
        Assert.Contains("yavnyy nol", run);   // отсутствие событий печатается явным нулём
    }

    [Fact]
    public void EventsProbe_UsesUnifiedThirtyDayWindowAndPrintsJournalDepth()
    {
        // Регрессия (бэклог п.123, СЗ 161346): машина приехала в сервис через две недели
        // после последнего вырубона, а зашитое в рецепте окно в 14 дней вернуло пустоту
        // там, где в журнале лежало 25 событий. Окно унифицировано на 30 дней (как в
        // kp41-detail.ps1/whea-storage-detail.ps1) и печатается явно, вместе с глубиной
        // журнала — «пусто» не должно читаться как «дефекта нет».
        var run = Body("events");

        Assert.Contains("SZ_EVENT_WINDOW_DAYS = 30", run);
        Assert.Contains("Write-EventWindowNote", run);
        Assert.DoesNotContain("AddDays(-7)", run);
        Assert.DoesNotContain("AddDays(-3)", run);
    }

    [Fact]
    public void RebootsAndWheaProbes_PrintJournalDepth()
    {
        // Те же секции читают историю ЦЕЛИКОМ без окна ("FULL HISTORY") — но "0 событий"
        // там тоже нельзя отличить от "журнал короче, чем кажется" без явной глубины.
        foreach (var section in new[] { "reboots", "whea" })
            Assert.Contains("Write-JournalDepthNote", Body(section));
    }

    [Fact]
    public void EventsProbe_PrintsTimeZoneLabelForOfflineComparisons()
    {
        // Регрессия (п.31/49): WinPE и хост живут в разных поясах (159948: -08:00 vs +03:00,
        // разница 11 часов молча превращала "днём" в "ночью"). Метка обязана быть явной.
        var run = Body("events");

        Assert.Contains("Write-TzNote", run);
        Assert.Contains("WinPE=", run);
    }

    [Fact]
    public void RebootsProbe_ComparesUnsafeShutdownsWithJournal()
    {
        // Регрессия (бэклог п.142): Unsafe Shutdowns накопителя - независимое от журнала ОС
        // доказательство отказа (67 счётчика против 73 Kernel-Power 41 на 160705). Раньше
        // добывалось только рецептом; теперь должно быть прямо рядом с таймлайном reboots.
        var run = Body("reboots");

        Assert.Contains("Get-NvmeSmartRows", run);
        Assert.Contains("Unsafe Shutdowns", run);
        Assert.Contains("zhurnal OS (Kernel-Power 41)", run);
    }

    [Fact]
    public void RebootsAndWheaProbes_AlsoPrintTimeZoneLabel()
    {
        // Регрессия (п.49): PE и хост могут жить в разных поясах не только в events -
        // timeline вырубонов и WHEA страдает от того же расхождения.
        foreach (var section in new[] { "reboots", "whea" })
            Assert.Contains("Write-TzNote", Body(section));
    }

    [Fact]
    public void ProbesQueryingOptionalProviders_WrapCallsInTryCatch()
    {
        // Живая грабля: незарегистрированный ProviderName валит Get-WinEvent с
        // 'The parameter is incorrect' ДАЖЕ при -ErrorAction SilentlyContinue.
        foreach (var section in new[] { "livekernel", "storage" })
        {
            var run = Body(section);
            Assert.DoesNotContain("ProviderName=$p } -ErrorAction SilentlyContinue", run);
            Assert.DoesNotContain("ProviderName=$prov } -ErrorAction SilentlyContinue", run);
            Assert.Contains("catch { }", run);
        }
    }

    [Fact]
    public void AllProbeBodies_AreAscii()
    {
        // Тела проб — строго ASCII (см. комментарий класса): русские заголовки живут в Name.
        foreach (var step in DiagnosticProbes.Suite.Steps)
            Assert.All(step.Run!, c => Assert.True(c < 128, $"не-ASCII в секции {step.Id}: {c}"));
    }

    [Fact]
    public void Suite_SectionIdsAreUnique()
    {
        var ids = DiagnosticProbes.Suite.Steps.Select(s => s.Id!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}

public class DiagReportRunnerTests
{
    private sealed class FakeExecutor : ICommandExecutor
    {
        public CommandResult Run(string command) => new(0, "OK", "");
    }
    private sealed class FakeCapturer : IScreenCapturer
    {
        public ScreenCapture Capture() => new(new byte[] { 1 }, null);
    }
    private sealed class CapturingLink : IHubLink
    {
        public List<UploadReportPart> Uploaded { get; } = new();
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null, string? lastShutdown = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportPowerEventsAsync(SzDiag.Contracts.PowerEventsReport report, CancellationToken ct = default) => Task.CompletedTask;
        public Task HeartbeatAsync(string sz, CancellationToken ct = default) => Task.CompletedTask;
        public void OnRevert(Func<string, Task> handler) { }
        public void OnRunTests(Func<string, string?, Task> handler) { }
        public void OnRunDiag(Func<string, string?, Task> handler) { }
        public Func<SzDiag.Contracts.ExecRequest, Task>? ExecHandler { get; private set; }
        public List<SzDiag.Contracts.ExecResult> ExecResults { get; } = new();
        public void OnExec(Func<SzDiag.Contracts.ExecRequest, Task> handler) => ExecHandler = handler;
        public Task SendExecResultAsync(SzDiag.Contracts.ExecResult result, CancellationToken ct = default) { ExecResults.Add(result); return Task.CompletedTask; }
        public Task SendExecAckAsync(SzDiag.Contracts.ExecAck ack, CancellationToken ct = default) => Task.CompletedTask;
        public void OnExecStatus(Func<SzDiag.Contracts.ExecStatusRequest, Task> handler) { }
        public Task SendExecJobStatusAsync(SzDiag.Contracts.ExecJobStatus status, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPush(Func<SzDiag.Contracts.PushRequest, Task> handler) { }
        public Task SendPushResultAsync(SzDiag.Contracts.PushResult result, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPull(Func<SzDiag.Contracts.PullRequest, Task> handler) { }
        public Task SendPullAckAsync(SzDiag.Contracts.PullAck ack, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullChunkAsync(SzDiag.Contracts.PullChunk chunk, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullResultAsync(SzDiag.Contracts.PullResult result, CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadReportFileAsync(UploadReportPart part, CancellationToken ct = default)
        {
            Uploaded.Add(part);
            return Task.CompletedTask;
        }
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendRevertResultAsync(SzDiag.Contracts.RevertResult result, CancellationToken ct = default) => Task.CompletedTask;
        public void OnRestartAgent(Action<string> handler) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DiagReportRunner Make(CapturingLink link) => new(
        new TestRunner(new FakeExecutor(), new FakeCapturer()),
        DiagnosticProbes.Suite, link, "PC-1",
        () => new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task RunAndUpload_Full_UploadsSingleDiagMd()
    {
        var link = new CapturingLink();
        var outcome = await Make(link).RunAndUploadAsync("156864");

        Assert.True(outcome.Ran);
        var part = Assert.Single(link.Uploaded);
        Assert.Equal("diag.md", part.FileName);
        Assert.Equal("156864", part.Sz);
        Assert.Equal("20260721-100000", part.Timestamp);
    }

    [Fact]
    public async Task RunAndUpload_Sections_ReportContainsOnlySelected()
    {
        var link = new CapturingLink();
        await Make(link).RunAndUploadAsync("156864", "storage,gpu");

        var md = System.Text.Encoding.UTF8.GetString(Assert.Single(link.Uploaded).Content);
        Assert.Contains("Диски", md);        // storage
        Assert.Contains("Видеокарта", md);   // gpu
        Assert.DoesNotContain("Процессор", md); // cpu не выбран
    }

    [Fact]
    public async Task RunAndUpload_UnknownSection_DoesNotRunOrUpload()
    {
        var link = new CapturingLink();
        var outcome = await Make(link).RunAndUploadAsync("156864", "nope");

        Assert.False(outcome.Ran);
        Assert.Empty(link.Uploaded);
        Assert.Contains("storage", outcome.AvailableSections);
    }
}
