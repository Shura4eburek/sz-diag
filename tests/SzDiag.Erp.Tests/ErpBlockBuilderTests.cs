namespace SzDiag.Erp.Tests;

public class ErpBlockBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 14, 30, 0, TimeSpan.FromHours(3));

    private static string Build(string json) => ErpBlockBuilder.Build(ErpJson.ParseSzFetch(Fixture.Of(json)), Now);

    [Fact]
    public void Блок_несёт_дату_обновления()
    {
        var block = ErpBlockBuilder.Build(ErpJson.ParseSzFetch(Fixture.SzFetch()), Now);

        Assert.Contains("2026-09-02 14:30", block);
    }

    [Fact]
    public void Поля_заявки_печатаются_как_есть()
    {
        var block = Build("""
            {"request":{"number":"160800","fields":{"Дефект":"гасне під грою","Вимога":"діагностика"},
             "components":[],"discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        Assert.Contains("**Дефект:** гасне під грою", block);
        Assert.Contains("**Вимога:** діагностика", block);
    }

    [Fact]
    public void Пустые_поля_не_печатаются()
    {
        // В живом ответе 37 полей, больше половины пустые — печатать их значит утопить
        // дефект в шуме.
        var block = Build("""
            {"request":{"number":"160800","fields":{"Дефект":"є","Коментар":"","ТТН":""},
             "components":[],"discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        Assert.Contains("**Дефект:**", block);
        Assert.DoesNotContain("Коментар", block);
        Assert.DoesNotContain("ТТН", block);
    }

    [Fact]
    public void Многострочное_поле_схлопывается_в_одну_строку()
    {
        var block = Build("""
            {"request":{"number":"160800","fields":{"Дефект":"перший рядок\nдругий рядок"},
             "components":[],"discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        Assert.Contains("**Дефект:** перший рядок другий рядок", block);
    }

    [Fact]
    public void Состав_из_комплектации_печатается_с_серийниками()
    {
        var block = Build("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"Відеокарта X","serial":"SN000000000001","quantity":"1"}],
             "discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        Assert.Contains("Серійний номер", block);
        Assert.Contains("| Відеокарта X | SN000000000001 | 1 |", block);
    }

    [Fact]
    public void Состав_из_заказа_печатается_с_ценой_и_гарантией_а_не_с_пустыми_серийниками()
    {
        // В строках заказа серийников нет — колонка была бы пустой на всю таблицу.
        // Зато есть цена и гарантия, а «гарантийный ли ремонт» — рабочий вопрос сервиса.
        var block = Build("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],
             "order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[
               {"Код":"1","Товар":"Процесор AMD","Шт.":"1","Ціна":"17999.00","Гарантія":"3 роки"}],
             "service_requests":[]},"assembly":null}
            """);

        Assert.DoesNotContain("Серійний номер", block);
        Assert.Contains("Гарантія", block);
        Assert.Contains("| Процесор AMD | 1 | 17999.00 | 3 роки |", block);
    }

    [Fact]
    public void Заголовок_состава_называет_источник()
    {
        var fromOrder = Build("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],
             "order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[{"Товар":"Процесор","Шт.":"1"}],
             "service_requests":[]},"assembly":null}
            """);
        var fromAssembly = Build("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],
             "order_number":null},"order":null,
             "assembly":{"number":"46323","summary":"зведення",
             "components":[{"code":"A1","name":"Зі збірки","serial":"SN2","quantity":"1"}]}}
            """);

        Assert.Contains("замовлення 1951256", fromOrder);
        Assert.Contains("збірки 46323", fromAssembly);
    }

    [Fact]
    public void Пустой_состав_даёт_пояснение_а_не_пустую_таблицу()
    {
        var block = Build("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":null},
             "order":null,"assembly":null}
            """);

        Assert.Contains("склад невідомий", block);
    }

    [Fact]
    public void Переписка_в_блок_не_попадает()
    {
        var block = Build("""
            {"request":{"number":"160800","fields":{},"components":[],
             "discussion":[{"date":"2026-09-01","author":"оператор","text":"СЕКРЕТНЕ ЛИСТУВАННЯ"}],
             "order_number":null},"order":null,"assembly":null}
            """);

        Assert.DoesNotContain("СЕКРЕТНЕ ЛИСТУВАННЯ", block);
    }

    [Fact]
    public void Труба_в_значении_не_ломает_таблицу()
    {
        var block = Build("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"ОЗП 2x16 | DDR5","serial":"SN1","quantity":"2"}],
             "discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        Assert.Contains(@"ОЗП 2x16 \| DDR5", block);
    }

    [Fact]
    public void На_реальной_фикстуре_состав_не_пустой()
    {
        var block = ErpBlockBuilder.Build(ErpJson.ParseSzFetch(Fixture.SzFetch()), Now);

        Assert.DoesNotContain("склад невідомий", block);
        Assert.Contains("Гарантія", block);
    }
}
