namespace TallyAgent.Core.Notifications;

/// <summary>Fixed error taxonomy used across SQLite, heartbeats and alerts.</summary>
public enum ErrorCategory
{
    TallyNotRunning,
    TallyPortUnavailable,
    TallyCompanyNotOpen,
    TallyInvalidXml,
    TallyTimeout,
    InternetUnavailable,
    CloudApiUnavailable,
    AuthenticationFailure,
    UploadFailure,
    LocalDatabaseFailure,
    DiskSpaceLow,
    SchemaMismatch,
    ServiceStopped,
    UnexpectedException,
}

public enum ErrorSeverity { Warning, Error, Critical }
