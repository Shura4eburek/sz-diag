namespace SzDiag.Contracts;

/// <summary>Перезапуск агента на клиенте без похода к машине.
///
/// Боль (бэклог п.83, СЗ 160306): после отката по watchdog доступа не было, а поднять агента
/// заново оказалось нечем. `--resume` не годится (`state.json` удалён), номер СЗ обычный режим
/// спрашивал только с консоли, а попытка сделать это скриптом через `exec` **потеряла машину**:
/// скрипт выполняется дочерним процессом агента, и первый же `Stop-Process` убил и агента, и
/// сам скрипт — задача создаться не успела.
///
/// Отсюда порядок: **сначала** ставится отложенная задача под SYSTEM (она переживёт смерть
/// агента), её создание проверяется, и только она затем гасит старый процесс и поднимает
/// новый. Агент себя не убивает вообще.</summary>
public static class AgentRestart
{
    /// <summary>Имя задачи-перезапуска. Префикс общий с остальными — их ищет уборка следов.</summary>
    public static string TaskName(string sz) => $"szdiag-restart-{sz}";

    /// <summary>Скрипт, который агент выполняет у себя (через `szcli exec --detach`).
    /// Ничего не убивает: только регистрирует задачу и печатает результат проверки.</summary>
    /// <param name="delaySeconds">Через сколько задача сработает. 45 с — чтобы ответ успел
    /// уехать на hub до того, как процесс агента будет снят.</param>
    public static string BuildScript(string sz, int delaySeconds = 45)
    {
        var task = TaskName(sz);
        // Путь к exe берём у живого процесса: на клиентах агент лежит где угодно
        // (в том числе внутри OneDrive — см. п.63), хардкодить нельзя.
        return $$"""
            $ErrorActionPreference = 'Stop'
            $proc = Get-Process -Name 'SzDiag.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $proc) { 'ОШИБКА: процесс агента не найден — перезапускать нечего.'; exit 1 }
            $exe = $proc.Path
            if (-not $exe) { 'ОШИБКА: не удалось определить путь к agent.exe.'; exit 1 }

            $inner = "Stop-Process -Name 'SzDiag.Agent' -Force -ErrorAction SilentlyContinue; " +
                     "Start-Sleep -Seconds 5; " +
                     "Start-Process -FilePath '$exe' -ArgumentList '{{sz}}'"
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
                -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command `"$inner`""
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds({{delaySeconds}})
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            Register-ScheduledTask -TaskName '{{task}}' -Action $action -Trigger $trigger `
                -Principal $principal -Force | Out-Null

            # Проверяем, что задача РЕАЛЬНО создана: именно её отсутствие и стоило потери
            # машины в прошлый раз.
            $check = Get-ScheduledTask -TaskName '{{task}}' -ErrorAction SilentlyContinue
            if ($check) {
                "ОК: задача {{task}} создана, агент перезапустится через {{delaySeconds}} с (exe: $exe)"
            } else {
                'ОШИБКА: задачу создать не удалось — агент НЕ трогаем, доступ остаётся как есть.'
                exit 1
            }
            """;
    }

    /// <summary>Скрипт уборки задачи-перезапуска: после успешного подъёма она не нужна.
    /// Гоняется агентом при старте — иначе задача останется следом на машине клиента.</summary>
    public static string BuildCleanupScript(string sz) =>
        $"Unregister-ScheduledTask -TaskName '{TaskName(sz)}' -Confirm:$false -ErrorAction SilentlyContinue";
}
