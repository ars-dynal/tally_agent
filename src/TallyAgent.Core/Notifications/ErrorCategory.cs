namespace TallyAgent.Core.Notifications;

/// <summary>Fixed error taxonomy used across SQLite, heartbeats and alerts.</summary>
public enum ErrorCategory
{
    TallyNotRunning,
    TallyPortUnavailable,
    TallyCompanyNotOpen,
    TallyInvalidXml,
    TallyTimeout,
    TallyBusy,
    TallyCompanyMismatch,
    TallyResponseTooLarge,
    TallyPreflightCancelled,
    InternetUnavailable,
    CloudApiUnavailable,
    AuthenticationFailure,
    UploadFailure,
    LocalDatabaseFailure,
    DiskSpaceLow,
    SchemaMismatch,
    ServiceStopped,
    UnexpectedException,
    /// <summary>Tally answered HTTP 200 with well-formed XML that REFUSES the
    /// request — "Unknown Request, cannot be processed", or a TDL LINEERROR.
    /// Appended rather than grouped with the other Tally* values so no stored
    /// ordinal shifts.</summary>
    TallyRequestRejected,
}

public enum ErrorSeverity { Warning, Error, Critical }
