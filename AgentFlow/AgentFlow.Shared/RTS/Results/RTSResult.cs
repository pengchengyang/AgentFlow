using AgentFlow.Shared.RTS.Errors;

namespace AgentFlow.Shared.RTS.Results;

/// <summary>Business result for operations that do not return a value.</summary>
public readonly record struct RTSResult(RTSErrorCode Code, string Message)
{
    public bool IsSuccess => Code == RTSErrorCode.Success;

    public static RTSResult Success(string message = "") => new(RTSErrorCode.Success, message);

    public static RTSResult Failure(RTSErrorCode code, string message)
    {
        if (code == RTSErrorCode.Success)
            throw new ArgumentException("A failure result cannot use RTSErrorCode.Success.", nameof(code));

        return new(code, message);
    }
}

/// <summary>Business result for operations that return a value.</summary>
public readonly record struct RTSResult<T>(RTSErrorCode Code, T? Value, string Message)
{
    public bool IsSuccess => Code == RTSErrorCode.Success;

    public static RTSResult<T> Success(T value, string message = "") =>
        new(RTSErrorCode.Success, value, message);

    public static RTSResult<T> Failure(RTSErrorCode code, string message)
    {
        if (code == RTSErrorCode.Success)
            throw new ArgumentException("A failure result cannot use RTSErrorCode.Success.", nameof(code));

        return new(code, default, message);
    }
}
