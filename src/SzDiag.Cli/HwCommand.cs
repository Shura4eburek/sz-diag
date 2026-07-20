using SzDiag.Hardware;

namespace SzDiag.Cli;

/// <summary>Подкоманды `hw import` / `hw update` / `hw resolve`. Тонкий слой над SzDiag.Hardware.</summary>
public static class HwCommand
{
    private const string PciIdsUrl = "https://pci-ids.ucw.cz/v2.2/pci.ids";

    public static async Task RunAsync(string[] args, string dbPath, string pciIdsPath)
    {
        var repo = new GpuRepository($"Data Source={dbPath}");
        await repo.InitializeAsync();
        var sub = args[0].ToLowerInvariant();

        if (sub == "import")
        {
            var path = args.Length >= 2 ? args[1] : pciIdsPath;
            if (!File.Exists(path))
            {
                Console.WriteLine($"Не найден pci.ids: {path}. Скачай через `szcli hw update`.");
                return;
            }
            await ImportFileAsync(repo, path);
            return;
        }

        if (sub == "update")
        {
            Console.WriteLine($"Качаю {PciIdsUrl} …");
            using var http = new HttpClient();
            var text = await http.GetStringAsync(PciIdsUrl);
            await File.WriteAllTextAsync(pciIdsPath, text);
            await ImportFileAsync(repo, pciIdsPath);
            return;
        }

        if (sub == "resolve" && args.Length >= 2)
        {
            PciId id;
            try { id = PciId.Parse(args[1]); }
            catch (FormatException ex) { Console.WriteLine(ex.Message); return; }

            var res = await new GpuResolver(repo, new NotImplementedGpuScraper()).ResolveAsync(id);
            Print(res);
            return;
        }

        Console.WriteLine("""
            Использование:
              szcli hw import [<путь к pci.ids>]   импорт локального файла в базу
              szcli hw update                      скачать свежий pci.ids и импортировать
              szcli hw resolve "<PCI ID>"          определить видяху по hardware id
            """);
    }

    private static async Task ImportFileAsync(GpuRepository repo, string path)
    {
        var data = PciIdsParser.Parse(await File.ReadAllTextAsync(path));
        await repo.ImportAsync(data);
        Console.WriteLine($"Импортировано: вендоров {data.Vendors.Count}, устройств {data.Devices.Count}.");
    }

    private static void Print(GpuResolution r)
    {
        var vendor = r.VendorName ?? "неизвестен";
        var model = r.Model ?? "не определена";
        var chip = r.Chip ?? "—";
        var partner = r.SubVendorName ?? (r.SubVendorId ?? "—");
        var source = r.Source switch
        {
            GpuSource.Cache => "локальная база (pci.ids)",
            GpuSource.Scraper => "скрапер",
            _ => "не определён (device нет в базе; попробуй `hw update`)"
        };
        Console.WriteLine($"""
              Вендор:   {vendor} ({r.VendorId})
              Модель:   {model}
              Чип:      {chip}
              Партнёр:  {partner}{(r.SubVendorId is null ? "" : $" ({r.SubVendorId})")}
              Ревизия:  {r.Revision ?? "—"}
              Источник: {source}
            """);
    }
}
