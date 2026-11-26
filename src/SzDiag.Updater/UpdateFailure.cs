using System.Net;

namespace SzDiag.Updater;

/// <summary>Почему не удалось спросить версию у hub. Раньше любой сбой печатался как «hub не
/// поддерживает апдейт»: чужой токен и недоступная сеть выглядели как устаревший hub.</summary>
public static class UpdateFailure
{
    public static string Describe(HttpRequestException ex) => ex.StatusCode switch
    {
        HttpStatusCode.NotFound => "Hub не поддерживает апдейт (нет /agent/version): старая версия hub.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"Hub отклонил токен (HTTP {(int)ex.StatusCode}): AgentToken в appsettings.json из другой сборки hub. " +
            "Нужен appsettings.json из свежего client-bootstrap.zip.",
        { } code => $"Hub ответил ошибкой HTTP {(int)code} на /agent/version.",
        null => $"Нет связи с hub: {Chain(ex)}. Проверь адрес, DNS, прокси/VPN и системное время.",
    };

    /// <summary>Настоящая причина (DNS, TLS, отказ соединения) лежит во вложенных исключениях.</summary>
    private static string Chain(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" <- ", parts.Distinct());
    }
}
