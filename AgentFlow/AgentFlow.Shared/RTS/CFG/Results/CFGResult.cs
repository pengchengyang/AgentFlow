using AgentFlow.Shared.RTS.CFG.Errors;

namespace AgentFlow.Shared.RTS.CFG.Results;

/// <summary>Business result for operations that do not return a value.</summary>
public readonly record struct CFGResult(CFGErrorCode Code, string Message)
{
    public bool IsSuccess => Code == CFGErrorCode.Success;

    public static CFGResult Success(string message = "") => new(CFGErrorCode.Success, message);

    public static CFGResult Failure(CFGErrorCode code, string message)
    {
        if (code == CFGErrorCode.Success)
            throw new ArgumentException("A failure result cannot use CFGErrorCode.Success.", nameof(code));

        return new(code, message);
    }
}

/// <summary>Business result for operations that return a value.</summary>
public readonly record struct CFGResult<T>(CFGErrorCode Code, T? Value, string Message)
{
    public bool IsSuccess => Code == CFGErrorCode.Success;

    public static CFGResult<T> Success(T value, string message = "") =>
        new(CFGErrorCode.Success, value, message);

    public static CFGResult<T> Failure(CFGErrorCode code, string message)
    {
        if (code == CFGErrorCode.Success)
            throw new ArgumentException("A failure result cannot use CFGErrorCode.Success.", nameof(code));

        return new(code, default, message);
    }
}