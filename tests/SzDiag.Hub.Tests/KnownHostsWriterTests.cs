using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Имя quick tunnel'а публично и не аутентифицировано: кто узнал его, тот может
/// встать посередине. Без пиннинга host-ключа подмену не отличить, а ходили мы со
/// StrictHostKeyChecking=no. Якорь доверия — управляющий канал: ключ приезжает оттуда,
/// а не с того конца, к которому подключаемся.</summary>
public class KnownHostsWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "szdiag-kh-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Пишет_строку_в_формате_known_hosts()
    {
        var path = KnownHostsWriter.Write(_root, "162003", "aaa-bbb.trycloudflare.com",
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI");

        var line = File.ReadAllText(path).Trim();
        Assert.StartsWith("aaa-bbb.trycloudflare.com ssh-ed25519 ", line);
    }

    [Fact]
    public void Новое_имя_туннеля_перезаписывает_файл_а_не_копит()
    {
        // После ребута имя ДРУГОЕ. Если копить, ssh рано или поздно упрётся в несовпадение.
        KnownHostsWriter.Write(_root, "162003", "старое.trycloudflare.com", "ssh-ed25519 AAAA1");
        var path = KnownHostsWriter.Write(_root, "162003", "новое.trycloudflare.com", "ssh-ed25519 AAAA2");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("старое.trycloudflare.com", text);
        Assert.Contains("новое.trycloudflare.com", text);
    }

    [Fact]
    public void У_каждой_СЗ_свой_файл()
    {
        var a = KnownHostsWriter.Write(_root, "162003", "a.trycloudflare.com", "ssh-ed25519 AAAA1");
        var b = KnownHostsWriter.Write(_root, "162004", "b.trycloudflare.com", "ssh-ed25519 AAAA2");

        Assert.NotEqual(a, b);
        Assert.DoesNotContain("b.trycloudflare.com", File.ReadAllText(a));
    }

    [Fact]
    public void Мусорный_номер_СЗ_не_уводит_запись_из_корня()
    {
        // Тот же класс дыры, что Critical-5: номер приходит от наименее доверенной машины.
        Assert.ThrowsAny<Exception>(() =>
            KnownHostsWriter.Write(_root, @"..\..\Windows\System32", "x.trycloudflare.com",
                "ssh-ed25519 AAAA1"));
    }

    [Fact]
    public void PathFor_даёт_тот_же_путь_что_и_запись()
    {
        // CLI обязан искать файл ровно там, куда его положил hub.
        var written = KnownHostsWriter.Write(_root, "162003", "a.trycloudflare.com", "ssh-ed25519 AAAA1");

        Assert.Equal(written, KnownHostsWriter.PathFor(_root, "162003"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* временная папка */ }
    }
}
