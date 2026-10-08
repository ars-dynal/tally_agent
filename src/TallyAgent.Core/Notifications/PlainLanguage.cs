using TallyAgent.Core.Tally;

namespace TallyAgent.Core.Notifications;

/// <summary>
/// Turns an error category into something an accountant can act on.
///
/// "TallyActivePeriodTooNarrow" means nothing to the person who has to fix it.
/// Every failure the operator can see gets one sentence saying what happened and
/// one saying what to do about it.
/// </summary>
public static class PlainLanguage
{
    public sealed record Explanation(string What, string Action);

    public static Explanation Describe(ErrorCategory category) => category switch
    {
        ErrorCategory.TallyNotRunning => new(
            "Tally is not answering on the configured address and port.",
            "Open TallyPrime on the server and set F1 > Settings > Connectivity so it acts as 'Both' or 'Server'."),

        ErrorCategory.TallyPortUnavailable => new(
            "Something is listening on Tally's port, but it is not Tally.",
            "Check the port in the agent's settings against F1 > Settings > Connectivity in Tally."),

        ErrorCategory.TallyCompanyNotOpen => new(
            "No company is open in Tally.",
            "Open the company in TallyPrime. The agent cannot read anything until it is loaded."),

        ErrorCategory.TallyCompanyMismatch => new(
            "The company open in Tally is not the one the agent is configured for.",
            "Open the configured company, or change the company name in the agent's settings to match."),

        ErrorCategory.TallyActivePeriodTooNarrow => new(
            "Tally's books do not cover the dates the agent asked for, so there is nothing there to read.",
            "In Tally press Alt+F2 and widen the period to cover the range being extracted, then run again."),

        ErrorCategory.TallyWindowNotHonoured => new(
            "Tally returned records dated outside the day that was asked for.",
            "Usually a leftover diagnostic left settings behind in the Tally session — restart TallyPrime and run again. If it persists, report it: the date scoping has regressed."),

        ErrorCategory.TallyRequestRejected => new(
            "Tally refused the request outright rather than returning data.",
            "The report or collection being asked for is not available in this Tally build. Report it — this needs a code change, not a settings change."),

        ErrorCategory.TallyTimeout => new(
            "Tally took too long to answer and the request was abandoned.",
            "Usually Tally was busy with something heavy. It retries automatically; if it repeats, run the sync outside office hours."),

        ErrorCategory.TallyBusy => new(
            "Another request to Tally was already in progress.",
            "Nothing to do — the agent serialises its requests and will retry."),

        ErrorCategory.TallyInvalidXml => new(
            "Tally's reply could not be read as valid XML.",
            "Usually one malformed record. Check the logs for the record named, and report it."),

        ErrorCategory.TallyResponseTooLarge => new(
            "Tally's reply was larger than the agent is allowed to hold in memory.",
            "Reduce the extraction window, or raise 'maxResponseMb' in the agent's settings."),

        ErrorCategory.TallyPreflightCancelled => new(
            "The run was stopped before it started talking to Tally.",
            "Normally the service shutting down. No action unless it repeats."),

        ErrorCategory.InternetUnavailable => new(
            "The server cannot reach the internet, so nothing can be uploaded.",
            "Extracted data is queued safely on disk and will upload by itself once the connection returns."),

        ErrorCategory.CloudApiUnavailable => new(
            "The cloud ingestion service is not responding.",
            "Data is queued on disk and will upload automatically. If this lasts hours, tell whoever runs the ingestion API."),

        ErrorCategory.AuthenticationFailure => new(
            "The cloud rejected the agent's credentials.",
            "The API token is wrong or expired. Update it in the agent's settings — uploads are paused until then."),

        ErrorCategory.UploadFailure => new(
            "A batch of data was rejected by the cloud.",
            "Check the error text for the reason; the batch stays on disk and can be retried."),

        ErrorCategory.SchemaMismatch => new(
            "The cloud rejected the shape of the data the agent sent.",
            "Agent and ingestion API are out of step. Report it — this needs a code change."),

        ErrorCategory.LocalDatabaseFailure => new(
            "The agent's own database on this machine could not be read or written.",
            "Check disk space and that no other copy of the agent is running."),

        ErrorCategory.DiskSpaceLow => new(
            "The server is low on disk space, so extraction has paused to protect the machine.",
            "Free space on the drive holding C:\\ProgramData, then run again."),

        ErrorCategory.ServiceStopped => new(
            "The agent service stopped while work was in progress.",
            "It resumes from its last checkpoint when restarted; no data is lost."),

        _ => new(
            "An unexpected error occurred.",
            "Check the log for details, and report it if it repeats."),
    };

    /// <summary>One line for the run-history "why did this dataset not load"
    /// column.</summary>
    public static string Explain(Exception ex) =>
        ex is TallyException tex ? Describe(tex.Category).What : ex.Message;

    public static Explanation Describe(string categoryName) =>
        Enum.TryParse<ErrorCategory>(categoryName, ignoreCase: true, out var c)
            ? Describe(c)
            : new("An unexpected error occurred.", "Check the log for details.");
}
