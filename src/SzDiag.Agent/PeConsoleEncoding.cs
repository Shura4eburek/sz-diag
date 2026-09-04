using System.Runtime.InteropServices;
using System.Text;

namespace SzDiag.Agent;

/// <summary>Кодировка вывода дочернего процесса в WinPE (бэклог п.228, СЗ 161498).
///
/// В обычной винде <see cref="PowerShellRunner"/> прибивает UTF-8 на обеих сторонах через
/// <c>[Console]::OutputEncoding</c> в шапке скрипта. В PE эта присваивание вешает
/// powershell.exe намертво (СЗ 159948, exec молчал до таймаута даже на скрипте из одной
/// строки) — поэтому в PE UTF-8 не включаем вовсе, а дочерний процесс пишет в активную
/// OEM-кодовую страницу консоли PE (cp437/cp866 и т.п.). Раньше это никак не читалось на
/// стороне агента, и кириллица в выводе рецепта из PE уезжала кракозябрами
/// (`=== ?????? szdiag-* ?? ??????-????`).
///
/// Фикс — читать stdout/stderr дочернего процесса как реальную OEM-кодировку (узнаём через
/// <c>GetOEMCP</c>), а не гадать: перекодирование целиком на стороне .NET
/// (<c>StandardOutputEncoding</c>/<c>StandardErrorEncoding</c>), без правки самого скрипта и
/// без присваивания <c>[Console]::OutputEncoding</c> в дочернем процессе.</summary>
public static class PeConsoleEncoding
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static bool _registered;
    private static readonly object Lock = new();

    /// <summary>Регистрирует провайдер legacy-кодовых страниц (437/866/…) — начиная с
    /// .NET Core они не встроены и требуют <c>System.Text.Encoding.CodePages</c>.
    /// Идемпотентно, безопасно вызывать многократно.</summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Lock)
        {
            if (_registered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }

    /// <summary>Активная OEM-кодовая страница консоли (то, чем реально пишет дочерний
    /// powershell.exe в PE) — <c>null</c>, если определить не удалось или кодировка не
    /// поддерживается рантаймом (тогда вызывающий код оставляет декодирование по умолчанию).</summary>
    public static Encoding? DetectOemEncoding()
    {
        try
        {
            EnsureRegistered();
            var codePage = (int)GetOEMCP();
            if (codePage <= 0) return null;
            return Encoding.GetEncoding(codePage);
        }
        catch
        {
            return null;   // codepage не зарегистрирован/недоступен - декодируем как раньше
        }
    }
}
