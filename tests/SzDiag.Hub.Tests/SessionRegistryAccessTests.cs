using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionRegistryAccessTests
{
    [Fact]
    public void Register_кладёт_имя_туннеля_в_сессию()
    {
        var reg = new SessionRegistry();

        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1",
            lanIp: "192.168.1.5", accessHost: "aaa-bbb.trycloudflare.com",
            accessMode: AccessMode.Tunnel);

        var info = reg.TryGetInfo("162003")!;
        Assert.Equal("aaa-bbb.trycloudflare.com", info.AccessHost);
        Assert.Equal(AccessMode.Tunnel, info.AccessMode);
        Assert.Equal("192.168.1.5", info.LanIp);
    }

    [Fact]
    public void SetAccess_обновляет_имя_после_ребута()
    {
        // Quick tunnel не сохраняет hostname между запусками: после ребута клиента имя ДРУГОЕ.
        // Если бы hub держал только имя из Register, target вёл бы на мёртвый туннель.
        var reg = new SessionRegistry();
        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1",
            accessHost: "старое-имя.trycloudflare.com", accessMode: AccessMode.Tunnel);

        var ok = reg.SetAccess("162003", "новое-имя.trycloudflare.com", AccessMode.Tunnel, null);

        Assert.True(ok);
        Assert.Equal("новое-имя.trycloudflare.com", reg.TryGetInfo("162003")!.AccessHost);
    }

    [Fact]
    public void SetAccess_освежает_heartbeat()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero));
        var reg = new SessionRegistry(time);
        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1");
        time.Advance(TimeSpan.FromMinutes(5));

        reg.SetAccess("162003", "имя.trycloudflare.com", AccessMode.Tunnel, null);

        Assert.Equal(time.GetUtcNow(), reg.TryGetInfo("162003")!.LastHeartbeat);
    }

    [Fact]
    public void SetAccess_по_неизвестной_СЗ_возвращает_false()
    {
        var reg = new SessionRegistry();

        Assert.False(reg.SetAccess("999999", "имя.trycloudflare.com", AccessMode.Tunnel, null));
    }

    [Fact]
    public void Туннель_отвалился_имя_обнуляется_но_сессия_жива()
    {
        var reg = new SessionRegistry();
        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1",
            accessHost: "имя.trycloudflare.com", accessMode: AccessMode.Tunnel);

        reg.SetAccess("162003", null, AccessMode.Direct, null);

        var info = reg.TryGetInfo("162003")!;
        Assert.Null(info.AccessHost);
        Assert.Equal(SessionStatus.Online, info.Status);
    }

    [Fact]
    public void SetAccess_не_затирает_ключ_пустым_значением()
    {
        // Агент сообщает смену имени туннеля, не трогая host-ключ: ключи переживают
        // перезапуск туннеля, и терять пиннинг из-за этого нельзя.
        var reg = new SessionRegistry();
        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1",
            accessHost: "первое.trycloudflare.com", accessMode: AccessMode.Tunnel,
            sshHostKeyFingerprint: "ssh-ed25519 AAAAC3Nz");

        reg.SetAccess("162003", "второе.trycloudflare.com", AccessMode.Tunnel, null);

        Assert.Equal("ssh-ed25519 AAAAC3Nz", reg.TryGetInfo("162003")!.SshHostKeyFingerprint);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
