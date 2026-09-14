using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Привязка «эта СЗ — точно этот агент». Токен `/agent/*` общий на весь флот,
/// поэтому Sz из тела сам по себе ничего не доказывает: заражённый клиент мог бы отчитаться
/// за чужую активную СЗ и выкинуть её из реестра (Critical-5).
///
/// Раньше сверяли IP вызова с IP регистрации. За Cloudflare Tunnel RemoteIpAddress у ВСЕХ
/// агентов одинаков (адрес cloudflared), и сверка совпадала бы всегда — дыра вернулась бы
/// целиком. Плюс прежняя проверка фейлилась ОТКРЫТО.</summary>
public class SessionSecretAuthTests
{
    private static SessionRegistry СРегистрацией(string sz, string ip)
    {
        var reg = new SessionRegistry();
        reg.Register(sz, ip, "PC", "conn-1");
        return reg;
    }

    [Fact]
    public void Свой_секрет_пропускается_даже_с_другого_IP()
    {
        // Агент мог переподключиться из другой сети — секрет, а не адрес, доказывает владение.
        var reg = СРегистрацией("162003", "10.0.0.1");
        var secret = reg.IssueSecret("162003");

        Assert.True(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "10.0.0.9", secret));
    }

    [Fact]
    public void Чужой_секрет_отклоняется_даже_с_того_же_IP()
    {
        var reg = СРегистрацией("162003", "127.0.0.1");
        reg.IssueSecret("162003");

        Assert.False(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "127.0.0.1", "чужой-секрет"));
    }

    [Fact]
    public void Секрет_не_предъявлен_при_живой_сессии_отклоняется()
    {
        // Fail closed — в отличие от прежней проверки.
        var reg = СРегистрацией("162003", "127.0.0.1");
        reg.IssueSecret("162003");

        Assert.False(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "127.0.0.1", null));
    }

    [Fact]
    public void За_туннелем_один_IP_не_даёт_отчитаться_за_чужую_СЗ()
    {
        // Прямая проверка того, что схема не воскрешает Critical-5: обе СЗ пришли с одного
        // адреса (cloudflared), но секреты разные.
        var reg = new SessionRegistry();
        reg.Register("162003", "127.0.0.1", "PC-1", "conn-1");
        reg.Register("162004", "127.0.0.1", "PC-2", "conn-2");
        reg.IssueSecret("162003");
        var чужой = reg.IssueSecret("162004");

        Assert.False(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "127.0.0.1", чужой));
    }

    [Fact]
    public void Агент_старой_сборки_откатывается_на_сверку_по_IP()
    {
        // Переходная ветка: у сессии секрета нет, потому что агент его не запрашивал.
        // Убрать вместе с веткой, когда апдейтер выведет флот.
        var reg = СРегистрацией("162003", "10.0.0.1");

        Assert.True(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "10.0.0.1", null));
        Assert.False(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "10.0.0.2", null));
    }

    [Fact]
    public void Сессии_нет_в_реестре_пропускаем()
    {
        // Обычный случай: watchdog шлёт отчёт как раз потому, что живого коннекта больше нет.
        var reg = new SessionRegistry();

        Assert.True(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "10.0.0.1", null));
    }

    [Fact]
    public void Секрет_у_каждой_СЗ_свой()
    {
        var reg = new SessionRegistry();
        reg.Register("162003", "10.0.0.1", "PC1", "conn-1");
        reg.Register("162004", "10.0.0.2", "PC2", "conn-2");

        Assert.NotEqual(reg.IssueSecret("162003"), reg.IssueSecret("162004"));
    }

    [Fact]
    public void Секрет_достаточно_длинный_чтобы_не_подбирался()
    {
        var reg = СРегистрацией("162003", "10.0.0.1");

        var secret = reg.IssueSecret("162003");

        Assert.True(secret.Length >= 32, $"секрет длиной {secret.Length} слишком короткий");
    }
}
