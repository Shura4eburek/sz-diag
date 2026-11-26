using System.Net;
using SzDiag.Updater;

namespace SzDiag.Updater.Tests;

public class UpdateFailureTests
{
    private static HttpRequestException Status(HttpStatusCode code) => new("x", null, code);

    [Fact]
    public void NotFound_MeansOldHub() => Assert.Contains("старая версия hub", UpdateFailure.Describe(Status(HttpStatusCode.NotFound)));

    [Fact]
    public void Unauthorized_MeansToken() => Assert.Contains("токен", UpdateFailure.Describe(Status(HttpStatusCode.Unauthorized)));

    [Fact]
    public void BadGateway_ShowsCode() => Assert.Contains("502", UpdateFailure.Describe(Status(HttpStatusCode.BadGateway)));

    [Fact]
    public void NoStatus_ShowsInnerReason()
    {
        var text = UpdateFailure.Describe(new HttpRequestException("внешняя", new IOException("хост не найден")));
        Assert.Contains("Нет связи", text);
        Assert.Contains("хост не найден", text);
    }
}
