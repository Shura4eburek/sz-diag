namespace SzDiag.Hub;

/// <summary>Запись файлов отчёта прогона в базу знаний.</summary>
public interface IReportStore
{
    /// <summary>Сохраняет файл в kb/СЗ/&lt;sz&gt;/reports/&lt;timestamp&gt;/. Возвращает полный путь.</summary>
    string Save(string sz, string timestamp, string fileName, byte[] content);
}
