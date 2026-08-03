using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class WinPeAccessManagerTests
{
    private static AccessSpec Spec(string sz = "156864") =>
        new(sz, "svc-diag", "ssh-ed25519 AAAA test", 22, TimeSpan.FromHours(1));

    [Fact]
    public void Open_возвращает_состояние_с_номером_СЗ()
    {
        var state = new WinPeAccessManager().Open(Spec("160176"));

        Assert.Equal("160176", state.Sz);
    }

    [Fact]
    public void Open_не_выставляет_ни_одного_флага_отката()
    {
        // Ключевой инвариант PE-режима: система не менялась, значит откатывать нечего.
        // Если сюда однажды добавят реальный шаг Open — тест упадёт и потребует парный Revert.
        var state = new WinPeAccessManager().Open(Spec());

        Assert.False(state.CreatedUser);
        Assert.False(state.StoppedSystemSshd);
        Assert.False(state.CreatedSshdTask);
        Assert.False(state.GeneratedHostKeys);
        Assert.False(state.AddedFirewallRule);
        Assert.False(state.WroteAuthorizedKey);
        Assert.False(state.CreatedAuthorizedKeysFile);
        Assert.False(state.SetTokenPolicy);
        Assert.False(state.CreatedWatchdogTask);
        Assert.False(state.CreatedAutostartTask);
    }

    [Fact]
    public void Revert_идемпотентен_и_не_бросает()
    {
        var manager = new WinPeAccessManager();
        var state = manager.Open(Spec());

        manager.Revert(state);
        manager.Revert(state);
    }

    [Fact]
    public void Resume_не_бросает()
    {
        var manager = new WinPeAccessManager();

        manager.Resume(manager.Open(Spec()), Spec());
    }
}
