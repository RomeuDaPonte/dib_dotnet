using Dib4t.Application.Common;

namespace Dib4t.Application.Tests.Common;

public sealed class ResultTests
{
    [Fact]
    public void Success_has_a_value_and_no_error()
    {
        Result<int> result = 5;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(5);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_has_an_error_and_no_value()
    {
        Result<int> result = Error.NotFound("x");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(new Error(ErrorKind.NotFound, "x"));
        var value = () => result.Value;
        value.Should().Throw<InvalidOperationException>().WithMessage("*x*");
    }

    [Theory]
    [InlineData(ErrorKind.Validation)]
    [InlineData(ErrorKind.NotFound)]
    [InlineData(ErrorKind.Conflict)]
    [InlineData(ErrorKind.BusinessRule)]
    [InlineData(ErrorKind.Failure)]
    public void Factories_set_the_kind(ErrorKind kind)
    {
        var error = kind switch
        {
            ErrorKind.Validation => Error.Validation("m"),
            ErrorKind.NotFound => Error.NotFound("m"),
            ErrorKind.Conflict => Error.Conflict("m"),
            ErrorKind.BusinessRule => Error.BusinessRule("m"),
            _ => Error.Failure("m"),
        };

        error.Should().Be(new Error(kind, "m"));
    }

    [Fact]
    public void Failure_needs_an_error() =>
        FluentActions.Invoking(() => Result<int>.Failure(null!)).Should().Throw<ArgumentNullException>();
}
