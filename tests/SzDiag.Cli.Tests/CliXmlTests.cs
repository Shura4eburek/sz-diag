using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

public class CliXmlTests
{
    private const string Sample =
        "#< CLIXML\r\n" +
        "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
        "<S S=\"Error\">At line:1 char:1_x000D__x000A_</S>" +
        "<S S=\"Error\">+ if (Test-Path )_x000D__x000A_</S>" +
        "<S S=\"Error\">+ ~~~~~~~~~~~~~~~_x000D__x000A_</S>" +
        "</Objs>";

    [Fact]
    public void Decode_ClixmlStderr_ReadableText()
    {
        var text = CliXml.Decode(Sample);

        Assert.DoesNotContain("CLIXML", text);
        Assert.DoesNotContain("_x000D_", text);
        Assert.Contains("At line:1 char:1", text);
        Assert.Contains("if (Test-Path )", text);
        Assert.Contains("\r\n", text);   // переносы вернулись на место
    }

    [Fact]
    public void Decode_PlainText_ReturnedAsIs()
    {
        const string plain = "обычный вывод\nвторая строка";
        Assert.Equal(plain, CliXml.Decode(plain));
    }

    [Fact]
    public void Decode_BrokenXml_ReturnsOriginalInsteadOfSwallowing()
    {
        // Съесть текст ошибки хуже, чем показать сырой.
        const string broken = "#< CLIXML\r\n<Objs><S>обрыв";
        Assert.Equal(broken, CliXml.Decode(broken));
    }

    [Fact]
    public void Unescape_LiteralUnderscoreX_RestoresName()
    {
        // Именно так gpushark_x64.exe приезжал как gpushark_x005F_x64.exe (бэклог п.24).
        Assert.Equal("gpushark_x64.exe", CliXml.Unescape("gpushark_x005F_x64.exe"));
    }

    [Theory]
    [InlineData("$p='C:\\tmp'; Test-Path $p", "$-переменные")]
    [InlineData("Get-ChildItem gpushark_x64.exe", "_x")]
    [InlineData("($_.Message -split \"`r?`n\")[0]", "бэктики")]
    [InlineData("строка1\nстрока2", "многострочный")]
    public void WarnAboutInline_RiskyScript_Warns(string script, string expectedFragment)
    {
        var warn = CliXml.WarnAboutInline(script);

        Assert.NotNull(warn);
        Assert.Contains(expectedFragment, warn);
        Assert.Contains("-f script.ps1", warn);
    }

    [Fact]
    public void WarnAboutInline_SimpleOneLiner_NoWarning()
        => Assert.Null(CliXml.WarnAboutInline("hostname"));
}
