namespace SzDiag.Erp.Tests;

public class ErpJsonTests
{
    [Fact]
    public void Заявка_разбирается_из_фикстуры()
    {
        var result = ErpJson.ParseSzFetch(Fixture.SzFetch());

        Assert.False(string.IsNullOrWhiteSpace(result.Request.Number));
        Assert.NotEmpty(result.Request.Fields);
    }

    [Fact]
    public void Заказа_может_не_быть()
    {
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":null},
             "order":null,"assembly":null}
            """);

        var result = ErpJson.ParseSzFetch(json);

        Assert.Null(result.Order);
        Assert.Null(result.Assembly);
        Assert.Null(result.Request.OrderNumber);
    }

    [Fact]
    public void Пустая_комплектация_это_пустой_список_а_не_ошибка()
    {
        // Так было на обеих живых заявках: гриды доступны не во всех статусах.
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[],"service_requests":[]},"assembly":null}
            """);

        var result = ErpJson.ParseSzFetch(json);

        Assert.Empty(result.Request.Components);
    }

    [Fact]
    public void Компоненты_читаются_с_серийниками()
    {
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"Відеокарта X","serial":"SN000000000001","quantity":"1"}],
             "discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        var component = Assert.Single(ErpJson.ParseSzFetch(json).Request.Components);

        Assert.Equal("Відеокарта X", component.Name);
        Assert.Equal("SN000000000001", component.Serial);
    }

    [Fact]
    public void Единственная_товарная_строка_даёт_название_а_несколько_нет()
    {
        var one = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[{"Код":"1","Товар":"ПК Ігровий","Шт.":"1"}],
             "service_requests":[]},"assembly":null}
            """);
        var many = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[
               {"Код":"1","Товар":"Материнська плата","Шт.":"1"},
               {"Код":"2","Товар":"Процесор","Шт.":"1"}],
             "service_requests":[]},"assembly":null}
            """);

        Assert.Equal("ПК Ігровий", ErpJson.ParseSzFetch(one).Order!.SingleProductName);
        Assert.Null(ErpJson.ParseSzFetch(many).Order!.SingleProductName);
    }

    [Fact]
    public void Состав_берётся_из_сборки_если_она_есть()
    {
        var withAssembly = Fixture.Of("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"З заявки","serial":"SN1","quantity":"1"}],
             "discussion":[],"order_number":"1951256"},"order":null,
             "assembly":{"number":"46323","summary":"зведення",
             "components":[{"code":"A1","name":"Зі збірки","serial":"SN2","quantity":"1"}]}}
            """);

        Assert.Equal("Зі збірки", Assert.Single(ErpJson.ParseSzFetch(withAssembly).Configuration).Name);
    }

    [Fact]
    public void Без_сборки_состав_берётся_из_комплектации_заявки()
    {
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"З заявки","serial":"SN1","quantity":"1"}],
             "discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        Assert.Equal("З заявки", Assert.Single(ErpJson.ParseSzFetch(json).Configuration).Name);
    }

    [Fact]
    public void Пустые_сборка_и_комплектация_дают_состав_из_заказа()
    {
        // Так было на ОБЕИХ живых заявках: комплектация и збірка пустые, весь состав
        // лежит в строках заказа. Это основной путь, а не запасной.
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],
             "order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[
               {"Код":"1","Товар":"Процесор","Шт.":"1"},
               {"Код":"2","Товар":"ОЗП","Шт.":"2"}],"service_requests":[]},
             "assembly":null}
            """);

        var configuration = ErpJson.ParseSzFetch(json).Configuration;

        Assert.Equal(2, configuration.Count);
        Assert.Equal("Процесор", configuration[0].Name);
        Assert.Equal("2", configuration[1].Quantity);
        // Серийников в строках заказа нет — и это не повод падать.
        Assert.Equal("", configuration[0].Serial);
    }

    [Fact]
    public void Фикстура_даёт_состав_из_заказа_а_не_пустоту()
    {
        // Регрессия на реальных данных: раньше Configuration возвращал пустоту,
        // потому что и збірка, и комплектация пришли пустыми.
        var result = ErpJson.ParseSzFetch(Fixture.SzFetch());

        Assert.Empty(result.Request.Components);
        Assert.Null(result.Assembly);
        Assert.NotEmpty(result.Configuration);
    }

    [Fact]
    public void Числа_в_полях_приводятся_к_строке()
    {
        // На той стороне числа и строки перемешаны; падать на этом нельзя.
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{"Сума":64499,"Прапорець":true},
             "components":[],"discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        var fields = ErpJson.ParseSzFetch(json).Request.Fields;

        Assert.Equal("64499", fields["Сума"]);
        Assert.Equal("True", fields["Прапорець"]);
    }
}
