namespace FACT.Shared.Errors;

/// <summary>
/// Shared FACT business error codes. Zero always means success; positive values identify failures.
/// Node-specific ranges keep independently owned errors from colliding.
/// </summary>
public enum FactErrorCode
{
    Success = 0,

    Unknown = 1,
    InvalidInput = 2,
    InvalidConfiguration = 3,
    InvalidState = 4,
    IoError = 5,
    ParseError = 6,
    Timeout = 7,
    ExternalServiceError = 8,

    // 1000-1999: FACTLauncher
    LauncherInvalidInput = 1001,
    LauncherInvalidJson = 1002,
    LauncherInvalidSlot = 1003,
    LauncherSlotMismatch = 1004,
    LauncherSettingsLoadFailed = 1005,
    LauncherSettingsIncomplete = 1006,
    LauncherSendCommandFailed = 1007,

    // 2000-2999: FACTLogCollector
    LogCollectorInvalidInput = 2001,
    LogCollectorDataSourceUnavailable = 2002,
    LogCollectorQueryFailed = 2003,
}
