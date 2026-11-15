using Dib4t.SharedKernel;

namespace Dib4t.Domain.Tests.SharedKernel;

/// <summary>ECMAScript <c>Number::toString</c> (the values are what Node 16 prints for <c>String(x)</c>).</summary>
public sealed class JsNumberTests
{
    [Theory]
    [InlineData(0d, "0")]
    [InlineData(10d, "10")]
    [InlineData(32.5, "32.5")]
    [InlineData(-12d, "-12")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    [InlineData(1e21, "1e+21")]
    [InlineData(123456789012345680000d, "123456789012345680000")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(1.5e-7, "1.5e-7")]
    [InlineData(2147483648d, "2147483648")]
    [InlineData(9007199254740993d, "9007199254740992")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(0.0015, "0.0015")]
    [InlineData(100d, "100")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void Writes_numbers_like_javascript(double value, string expected) => JsNumber.ToString(value).Should().Be(expected);

    [Fact]
    public void Negative_zero_is_zero() => JsNumber.ToString(-0d).Should().Be("0");
}
