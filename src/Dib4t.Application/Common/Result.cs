using System.Diagnostics.CodeAnalysis;

namespace Dib4t.Application.Common;

/// <summary>The outcome of a use case: a value or an <see cref="Common.Error"/> (PLAN.md §3.2).</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>The value of a successful result.</summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"The result is a failure: {Error.Message}");

#pragma warning disable CA1000 // static factory members on the generic type read best at the call site
    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
#pragma warning restore CA1000

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
