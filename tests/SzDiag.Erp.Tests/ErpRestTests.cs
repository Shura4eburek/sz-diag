using System.Text.Json;

namespace SzDiag.Erp.Tests;

/// <summary>
/// REST-путь TeleAuto. Формы ответов — с живого стенда (СЗ 163419, 161716, 161211),
/// персональные данные заменены.
/// </summary>
public class ErpRestTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "erp-rest-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
    }

    private static readonly JsonElement Prefab = Fixture.Of("""
        {"request":{"id":163419,"state_id":5,"order_id":1684629,"sn":"1521343-29721",
                    "stated_defect":"не вмикається горить на CPU","comment":"123",
                    "product_name":"HEXO Gaming RTX4060 Pro","apppearance":"Б/В"},
         "discussions":[{"id":1},{"id":2}],
         "repairs":[],
         "configuration":{"source":"prefab","series":"1521343-29721",
            "assembly":{"id":29721,"order_id":1521343},
            "components":[
              {"product_id":417530,"name":"AMD Ryzen 5 5600","quantity":1,
               "serial_numbers":["9AFE487U40336"],"category_id":531,"price":4168.0},
              {"product_id":374722,"name":"Kingston DDR4 16GB","quantity":1,
               "serial_numbers":[],"category_id":1127,"price":1436.0}],
            "serviced_item":{"product_id":698312,"name":"HEXO Gaming RTX4060 Pro","sn":"1521343-29721"}},
         "siblings":[{"id":142830,"state_id":6,"product_name":"HEXO Gaming RTX4060 Pro"}],
         "customer":{"phone":"0680000000","orders_total":1}}
        """);

    [Fact]
    public void Сводка_префаба_берёт_поля_заявки_и_состав()
    {
        var data = ErpRest.ParseSzGet(Prefab);

        Assert.Equal("163419", data.Number);
        Assert.Equal("Прийнята", data.State);
        Assert.Equal("1684629", data.OrderId);
        Assert.Equal("не вмикається горить на CPU", data.Defect);
        Assert.Equal("123", data.Comment);
        Assert.Equal("prefab", data.Source);
        Assert.Equal("1521343-29721", data.Series);
        Assert.Equal("29721", data.AssemblyId);
        Assert.Equal(2, data.Discussions);
        Assert.Equal(0, data.Repairs);
        Assert.NotNull(data.Components);
        Assert.Equal(["9AFE487U40336"], data.Components![0].Serials);
        Assert.Empty(data.Components[1].Serials);
        Assert.Equal("1", data.Components[0].Quantity);
    }

    [Fact]
    public void Повторные_СЗ_получают_название_статуса()
    {
        var sibling = Assert.Single(ErpRest.ParseSzGet(Prefab).Siblings);
        Assert.Equal(new RestSibling("142830", "Завершена", "HEXO Gaming RTX4060 Pro"), sibling);
    }

    [Fact]
    public void Без_нашей_сборки_состав_null_а_не_пустой()
    {
        // «Состава от нас нет» и «состав пуст» — разные ответы: первый ведёт к истории клиента.
        var data = ErpRest.ParseSzGet(Fixture.Of("""
            {"request":{"id":161211,"state_id":7,"order_id":1731912,"product_name":"RTX 5090"},
             "discussions":[],"repairs":[{"id":1}],
             "configuration":{"source":"no_our_assembly","series":null,"assembly":null,
               "components":null,"serviced_item":{"product_id":721170,"name":"Asus ROG Astral RTX 5090",
               "sn":"T8YVCM00L6007UP"}},
             "siblings":[],"customer":{"phone":"0970000000","orders_total":13}}
            """));

        Assert.Equal("no_our_assembly", data.Source);
        Assert.Null(data.Components);
        Assert.Null(data.Series);
        Assert.Null(data.AssemblyId);
        Assert.Equal("Asus ROG Astral RTX 5090", data.ServicedItem);
        Assert.Equal(13, data.CustomerOrdersTotal);
        Assert.Equal("0970000000", data.Phone);
    }

    [Fact]
    public void Без_телефона_подсказки_про_историю_нет()
    {
        var data = ErpRest.ParseSzGet(Fixture.Of("""
            {"request":{"id":1},"configuration":{"source":"our_build","components":[]},
             "customer":{"phone":null,"orders_total":null}}
            """));

        Assert.Null(data.Phone);
        Assert.Null(data.CustomerOrdersTotal);
        Assert.Empty(data.Siblings);
    }

    [Fact]
    public void Заказы_клиента_различают_товары_и_услуги()
    {
        var orders = ErpRest.ParseCustomerOrders(Fixture.Of("""
            {"phone":"0970000000","total":13,"orders":[
              {"id":1969150,"created_on":"2026-07-13T11:22:08","state_text":"Виконано","products":[
                {"name":"Гостевой ПК","quantity":1,"is_service":false},
                {"name":"Услуга ''Диагностика''","quantity":1,"is_service":true}]},
              {"id":1737150,"created_on":"2025-10-07T20:07:46","state_text":"Виконано","products":[
                {"name":"AMD Ryzen 7 9800X3D","quantity":1,"is_service":false}]}]}
            """));

        Assert.Equal("0970000000", orders.Phone);
        Assert.Equal(13, orders.Total);
        Assert.Equal(["1969150", "1737150"], orders.Orders.Select(o => o.Id));
        Assert.True(orders.Orders[0].Products[1].IsService);
        Assert.False(orders.Orders[0].Products[0].IsService);
        Assert.Equal("Виконано", orders.Orders[1].State);
    }

    [Theory]
    [InlineData("sz.get", true)]
    [InlineData("api.order", true)]
    [InlineData("api.customer.orders", true)]
    [InlineData("sz.fetch", false)]
    [InlineData("session.begin", false)]
    [InlineData("filters.set", false)]
    [InlineData("result.open", false)]
    public void Сквозной_вызов_открыт_только_REST(string tool, bool expected)
        => Assert.Equal(expected, ErpRest.IsCallable(tool));

    [Theory]
    [InlineData("161211", null, """{"number":"161211"}""")]
    [InlineData("161211", 20, """{"number":"161211","limit":20}""")]
    [InlineData("0977667583", null, """{"phone":"0977667583"}""")]
    [InlineData("+380 97 766 75 83", 5, """{"phone":"+380 97 766 75 83","limit":5}""")]
    public void Номер_СЗ_и_телефон_различаются_по_форме(string subject, int? limit, string expected)
        => Assert.Equal(expected, JsonSerializer.Serialize(ErpRest.CustomerOrdersArgs(subject, limit),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    [Fact]
    public void Сырой_ответ_ложится_рядом_с_заметками_и_не_трогает_erp_json()
    {
        var path = SzGetWriter.Save(_root, "163419", """{"request":{}}""");

        Assert.Equal(Path.Combine(_root, "СЗ", "163419", "erp-rest.json"), path);
        Assert.Equal("""{"request":{}}""", File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(_root, "СЗ", "163419", "erp.json")));
    }
}
