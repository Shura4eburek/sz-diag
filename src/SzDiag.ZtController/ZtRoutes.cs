using System.Text.RegularExpressions;

namespace SzDiag.ZtController;

/// <summary>Белый список того, что hub может делать с контроллером через прокси.
/// Всё остальное API zerotier-one (`/peer`, `/network` самой ноды, `/config`…) наружу не
/// выходит: прокси — единственная публичная дверь контейнера, а токен лежит на hub-машинах.</summary>
internal static partial class ZtRoutes
{
    // nwid: 10 hex адреса контроллера + 6 hex номера сети; при создании — 6 подчёркиваний
    // (контроллер выдаёт свободный номер сам).
    [GeneratedRegex("^/controller/network/[0-9a-f]{10}([0-9a-f]{6}|_{6})$")]
    private static partial Regex Network();

    [GeneratedRegex("^/controller/network/[0-9a-f]{16}/member$")]
    private static partial Regex Members();

    [GeneratedRegex("^/controller/network/[0-9a-f]{16}/member/[0-9a-f]{10}$")]
    private static partial Regex Member();

    public static bool IsAllowed(string method, string path)
    {
        var get = HttpMethods.IsGet(method);
        var post = HttpMethods.IsPost(method);
        var delete = HttpMethods.IsDelete(method);

        if (path is "/status" or "/controller" or "/controller/network") return get;
        if (Network().IsMatch(path))
            return path.EndsWith("______") ? post : get || post || delete;
        if (Members().IsMatch(path)) return get;
        if (Member().IsMatch(path)) return get || post || delete;
        return false;
    }
}
