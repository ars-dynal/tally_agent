using System.Windows;

namespace TallyAgent.Manager;

/// <summary>Management console for the Tally BigQuery Agent.
/// Closing this window NEVER affects the Windows Service — the service is
/// SCM-owned and runs with no user logged in.</summary>
public partial class App : Application
{
}
