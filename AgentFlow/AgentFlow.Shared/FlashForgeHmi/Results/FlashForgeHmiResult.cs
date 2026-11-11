using AgentFlow.Shared.FlashForgeHmi.Errors;

namespace AgentFlow.Shared.FlashForgeHmi.Results;

/// <summary>Business result for operations that do not return a value.</summary>
public readonly record struct FlashForgeHmiResult(FlashForgeHmiErrorCode Code, string Message)
{
    public bool IsSuccess => Code == FlashForgeHmiErrorCode.Success;

    public static FlashForgeHmiResult Success(string message = "") => new(FlashForgeHmiErrorCode.Success, message);

    public static FlashForgeHmiResult Failure(FlashForgeHmiErrorCode code, string message)
    {
        if (code == FlashForgeHmiErrorCode.Success)
            throw new ArgumentException("A failure result cannot use FlashForgeHmiErrorCode.Success.", nameof(code));

        return new(code, message);
    }
}

/// <summary>Business result for operations that return a value.</summary>
public readonly record struct FlashForgeHmiResult<T>(FlashForgeHmiErrorCode Code, T? Value, string Message)
{
    public bool IsSuccess => Code == FlashForgeHmiErrorCode.Success;

    public static FlashForgeHmiResult<T> Success(T value, string message = "") =>
        new(FlashForgeHmiErrorCode.Success, value, message);

    public static FlashForgeHmiResult<T> Failure(FlashForgeHmiErrorCode code, string message)
    {
        if (code == FlashForgeHmiErrorCode.Success)
            throw new ArgumentException("A failure result cannot use FlashForgeHmiErrorCode.Success.", nameof(code));

        return new(code, default, message);
    }
}
