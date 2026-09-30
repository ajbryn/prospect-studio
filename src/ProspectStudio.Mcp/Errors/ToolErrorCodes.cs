namespace ProspectStudio.Mcp.Errors;

public static class ToolErrorCodes
{
    public const string NotReady = "NOT_READY";
    public const string NotFound = "NOT_FOUND";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Conflict = "CONFLICT";
    public const string FileLocked = "FILE_LOCKED";
    public const string ExternalApi = "EXTERNAL_API";
    public const string RateLimited = "RATE_LIMITED";
    public const string LicenseBlocked = "LICENSE_BLOCKED";
    public const string JobRunning = "JOB_RUNNING";
    public const string Unsupported = "UNSUPPORTED";
    public const string Internal = "INTERNAL";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        NotReady,
        NotFound,
        ValidationFailed,
        Conflict,
        FileLocked,
        ExternalApi,
        RateLimited,
        LicenseBlocked,
        JobRunning,
        Unsupported,
        Internal,
    };
}
