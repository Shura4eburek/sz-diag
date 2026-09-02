using SzDiag.Kb;

namespace SzDiag.Erp.Tests;

public class SzFetchWriterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "erp-tests-" + Guid.NewGuid().ToString("N"));

    private readonly KbPaths _paths;

    public SzFetchWriterTests() => _paths = new KbPaths(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
    }

    private SzFetchWriter Writer() =>
        new(_root, () => new DateTimeOffset(2026, 9, 2, 14, 30, 0, TimeSpan.FromHours(3)));

    private static SzFetchResult Data(string product = "ПК Ігровий", string name = "") =>
        ErpJson.ParseSzFetch(Fixture.Of($$"""
            {"request":{"number":"160800","fields":{"Дефект":"гасне під грою","Назва":"{{name}}"},
             "components":[{"code":"K1","name":"Відеокарта X","serial":"SN000000000001","quantity":"1"}],
             "discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},
             "products":[{"Код":"1","Товар":"{{product}}","Шт.":"1"}],"service_requests":[]},
             "assembly":null}
            """));

    private string Home() => File.ReadAllText(_paths.HomeNote("160800"));

    [Fact]
    public void Сырой_ответ_ложится_в_папку_СЗ()
    {
        var result = Writer().Write("160800", Data(), """{"сирий":"json"}""", force: false);

        Assert.True(File.Exists(result.JsonPath));
        Assert.Contains("сирий", File.ReadAllText(result.JsonPath));
    }

    [Fact]
    public void Блок_попадает_в_запит()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        var request = File.ReadAllText(_paths.Request("160800"));
        Assert.Contains(MarkedBlock.Begin, request);
        Assert.Contains("SN000000000001", request);
    }

    [Fact]
    public void Ручной_текст_переживает_повторный_фетч()
    {
        Writer().Write("160800", Data(), "{}", force: false);
        File.AppendAllText(_paths.Request("160800"), "\nруками: перевірив БЖ, тримає\n");

        Writer().Write("160800", Data(), "{}", force: false);

        Assert.Contains("руками: перевірив БЖ, тримає", File.ReadAllText(_paths.Request("160800")));
    }

    [Fact]
    public void Пустой_frontmatter_заполняется()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        var home = FrontmatterEditor.Load(Home());
        Assert.Equal("1951256", home.GetScalar("замовлення")?.Trim('"'));
        Assert.Equal("ПК Ігровий", home.GetScalar("пристрій")?.Trim('"'));
    }

    [Fact]
    public void Заполненный_frontmatter_не_перебивается_без_force()
    {
        Writer().Write("160800", Data(), "{}", force: false);
        var edited = FrontmatterEditor.Load(Home());
        edited.SetScalar("пристрій", "\"Ноутбук вручну\"");
        File.WriteAllText(_paths.HomeNote("160800"), edited.Serialize());

        Writer().Write("160800", Data(product: "ПК Інший"), "{}", force: false);

        Assert.Equal("Ноутбук вручну", FrontmatterEditor.Load(Home()).GetScalar("пристрій")?.Trim('"'));
    }

    [Fact]
    public void Force_перебивает_заполненное_поле()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        Writer().Write("160800", Data(product: "ПК Інший"), "{}", force: true);

        Assert.Equal("ПК Інший", FrontmatterEditor.Load(Home()).GetScalar("пристрій")?.Trim('"'));
    }

    [Fact]
    public void Устройство_берётся_из_поля_Назва_а_не_из_строк_заказа()
    {
        // На живой заявке в заказе 12 позиций (услуги, монитор, термопаста), а предмет
        // заявки назван в поле «Назва». Считать строки бессмысленно.
        var data = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{"Назва":"Готова СВО Arctic"},
             "components":[],"discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[
               {"Код":"1","Товар":"Материнська плата","Шт.":"1"},
               {"Код":"2","Товар":"Процесор","Шт.":"1"}],"service_requests":[]},"assembly":null}
            """));

        var result = Writer().Write("160800", data, "{}", force: false);

        Assert.True(result.DeviceSet);
        Assert.Equal("Готова СВО Arctic", FrontmatterEditor.Load(Home()).GetScalar("пристрій")?.Trim('"'));
    }

    [Fact]
    public void Без_Назви_и_с_несколькими_позициями_устройство_не_ставится()
    {
        var data = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],
             "order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[
               {"Код":"1","Товар":"Материнська плата","Шт.":"1"},
               {"Код":"2","Товар":"Процесор","Шт.":"1"}],"service_requests":[]},"assembly":null}
            """));

        var result = Writer().Write("160800", data, "{}", force: false);

        Assert.False(result.DeviceSet);
        Assert.False(string.IsNullOrWhiteSpace(result.DeviceSkipReason));
    }

    [Fact]
    public void Заводятся_заметки_заказа_и_устройства()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        Assert.True(File.Exists(_paths.OrderNote("1951256")));
        Assert.True(File.Exists(_paths.DeviceNote("ПК Ігровий")));
    }

    [Fact]
    public void Компоненты_заметками_не_плодятся()
    {
        // Каждая сборка дала бы 8-12 заметок-однодневок, и поиск по vault утонул бы.
        Writer().Write("160800", Data(), "{}", force: false);

        Assert.False(File.Exists(_paths.ComponentNote("Відеокарта X")));
    }

    [Fact]
    public void В_журнал_ложится_строка()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        Assert.Contains("обліку", File.ReadAllText(_paths.Journal("160800")));
    }

    [Fact]
    public void Повторный_фетч_не_задваивает_блок()
    {
        Writer().Write("160800", Data(), "{}", force: false);
        Writer().Write("160800", Data(), "{}", force: false);

        var request = File.ReadAllText(_paths.Request("160800"));
        var count = request.Split(MarkedBlock.Begin).Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Реальная_фикстура_записывается_целиком()
    {
        var data = ErpJson.ParseSzFetch(Fixture.SzFetch());

        var result = Writer().Write("160800", data, "{}", force: false);

        Assert.True(File.Exists(result.JsonPath));
        Assert.Contains("Склад", File.ReadAllText(_paths.Request("160800")));
        Assert.True(result.DeviceSet);
    }
}
