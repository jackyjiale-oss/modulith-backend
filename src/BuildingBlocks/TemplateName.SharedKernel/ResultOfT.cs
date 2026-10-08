namespace TemplateName.SharedKernel;

/// <summary>A <see cref="Result"/> that carries a value when it succeeds.</summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>The success value. Throws <see cref="InvalidOperationException"/> when the result is a failure.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The value of a failed result cannot be accessed.");

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure<T>(error);
}
