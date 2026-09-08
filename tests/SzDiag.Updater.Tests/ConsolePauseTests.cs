using SzDiag.Updater;

namespace SzDiag.Updater.Tests;

/// <summary>Когда апдейтер запущен двойным кликом из проводника, окно консоли принадлежит
/// ему одному и закрывается вместе с процессом. На 161642 из-за этого целый день никто не
/// видел причину: guard честно печатал «запущен из облачной папки OneDrive», но текст
/// исчезал за долю секунды — а рабочий стол в Windows 11 по умолчанию и есть OneDrive.</summary>
public sealed class ConsolePauseTests
{
    [Fact]
    public void Ждём_нажатия_когда_окно_своё_и_вышли_с_ошибкой()
    {
        Assert.True(ConsolePause.ShouldWait(exitCode: 4, processesOnConsole: 1, inputRedirected: false));
    }

    [Fact]
    public void Не_ждём_после_успешного_прогона()
    {
        // Успех = агент отработал и вернул 0, держать окно незачем.
        Assert.False(ConsolePause.ShouldWait(exitCode: 0, processesOnConsole: 1, inputRedirected: false));
    }

    [Fact]
    public void Не_ждём_когда_запущено_из_готовой_консоли()
    {
        // В консоли есть cmd.exe/powershell.exe помимо нас — окно останется, вывод видно.
        Assert.False(ConsolePause.ShouldWait(exitCode: 4, processesOnConsole: 2, inputRedirected: false));
    }

    [Fact]
    public void Не_ждём_когда_ввод_перенаправлен()
    {
        // Запуск из скрипта/через updater-log.cmd с редиректом: пауза там повесит
        // автоматизацию намертво, а читать вывод всё равно будет файл, а не человек.
        Assert.False(ConsolePause.ShouldWait(exitCode: 4, processesOnConsole: 1, inputRedirected: true));
    }
}
