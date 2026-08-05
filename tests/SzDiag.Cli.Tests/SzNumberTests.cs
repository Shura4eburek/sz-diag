using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

public class SzNumberTests
{
    [Theory]
    [InlineData("160705")]
    [InlineData("111111")]
    [InlineData("000001")]
    public void IsValid_SixDigits_True(string sz) => Assert.True(SzNumber.IsValid(sz));

    [Theory]
    [InlineData("--help")]      // именно этот «номер» завёл папку СЗ/--help в живом vault
    [InlineData("-h")]
    [InlineData("123")]         // короткий
    [InlineData("1234567")]     // длинный
    [InlineData("abc")]
    [InlineData("16070a")]
    [InlineData("16070 ")]
    [InlineData(" 160705")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_Garbage_False(string? sz) => Assert.False(SzNumber.IsValid(sz));

    [Fact]
    public void Explain_Flag_SaysItIsAFlag()
        => Assert.Contains("флаг", SzNumber.Explain("--help"));

    [Fact]
    public void Explain_WrongLength_MentionsDigitCount()
        => Assert.Contains("6 цифр", SzNumber.Explain("123"));

    [Fact]
    public void Explain_Letters_MentionsDigitsOnly()
        => Assert.Contains("только из цифр", SzNumber.Explain("abcdef"));
}
