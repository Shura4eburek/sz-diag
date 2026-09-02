namespace SzDiag.Erp;

/// <summary>
/// Сбой обращения к API. <see cref="Code"/> — код с той стороны либо синтетический:
/// `unavailable` (сервис не отвечает или не настроен), `no_token` (нет файла токена),
/// `timeout` (не уложились в отведённое время).
/// </summary>
public sealed class ErpApiException : Exception
{
    public string Code { get; }

    public ErpApiException(string code, string message) : base(message) => Code = code;
}
