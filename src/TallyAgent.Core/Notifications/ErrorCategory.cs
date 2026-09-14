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
    /// <summary>The company's active period (Alt+F2) does not cover the range
    /// being extracted. Tally bounds every export by it regardless of
    /// SVFROMDATE/SVTODATE and returns a valid, EMPTY response outside it.</summary>
    TallyActivePeriodTooNarrow,
    /// <summary>Tally returned rows OUTSIDE the window that was asked for. Under
    /// a date-scoped mechanism that must never happen, so it means the scoping
    /// mechanism has regressed - not that the data is odd.</summary>
    TallyWindowNotHonoured,
}

public enum ErrorSeverity { Warning, Error, Critical }
