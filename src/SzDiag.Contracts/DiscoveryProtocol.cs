namespace SzDiag.Contracts;

/// <summary>
/// UDP-протокол обнаружения hub'а в локальной сети (отдельный порт от TCP/SignalR).
/// Агент шлёт broadcast-запрос с pre-shared токеном; hub отвечает unicast своим портом,
/// если токен совпал (иначе молчит — фильтр от постороннего сетевого шума).
/// </summary>
public static class DiscoveryProtocol
{
    public const int Port = 5098;

    private const string RequestPrefix = "SZDIAG-DISCOVER:";
    private const string ResponsePrefix = "SZDIAG-HUB:";

    public static string BuildRequest(string token) => RequestPrefix + token;

    public static bool TryParseRequest(string message, out string token)
    {
        if (message.StartsWith(RequestPrefix, StringComparison.Ordinal))
        {
            token = message[RequestPrefix.Length..];
            return true;
        }
        token = "";
        return false;
    }

    public static string BuildResponse(int hubPort) => ResponsePrefix + hubPort;

    public static bool TryParseResponse(string message, out int hubPort)
    {
        if (message.StartsWith(ResponsePrefix, StringComparison.Ordinal)
            && int.TryParse(message[ResponsePrefix.Length..], out hubPort))
            return true;
        hubPort = 0;
        return false;
    }
}
