using FACT.Shared.Errors;

namespace FACT.Shared.Results;

/// <summary>Business result for operations that do not return a value.</summary>
public readonly record struct FactResult(FactErrorCode Code, string Message)
{
    public bool IsSuccess => Code == FactErrorCode.Success;

    public static FactResult Success(string message = "") => new(FactErrorCode.Success, message);

    public static FactResult Failure(FactErrorCode code, string message)
    {
        if (code == FactErrorCode.Success)
            throw new ArgumentException("A failure result cannot use FactErrorCode.Success.", nameof(code));

        return new(code, message);
    }
}

/// <summary>Business result for operations that return a value.</summary>
public readonly record struct FactResult<T>(FactErrorCode Code, T? Value, string Message)
{
    public bool IsSuccess => Code == FactErrorCode.Success;

    public static FactResult<T> Success(T value, string message = "") =>
        new(FactErrorCode.Success, value, message);

    public static FactResult<T> Failure(FactErrorCode code, string message)
    {
        if (code == FactErrorCode.Success)
            throw new ArgumentException("A failure result cannot use FactErrorCode.Success.", nameof(code));

        return new(code, default, message);
    }
}
