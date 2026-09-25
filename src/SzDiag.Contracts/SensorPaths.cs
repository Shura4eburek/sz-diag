namespace SzDiag.Contracts;

/// <summary>Где на клиенте лежит CSV внешнего наблюдателя `lhmmon`. Одно место на CLI
/// (`sensors`) и Desk (вкладка «Сенсоры») — иначе разъедутся при первой смене пути.</summary>
public static class SensorPaths
{
    public const string LhmCsv = @"C:\OCCT\sensors.csv";
}
