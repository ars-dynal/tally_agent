namespace TallyAgent.Core;

/// <summary>Central version + well-known path constants for the agent.</summary>
public static class AgentInfo
{
    public const string Version = "2.0.4";
    public const string SchemaVersion = "1.0";
    public const string ServiceName = "TallyBigQueryAgent";
    public const string ServiceDisplayName = "Tally BigQuery Data Sync Agent";
    public const string EventLogSource = "TallyBigQueryAgent";

    /// <summary>C:\ProgramData\TallyBigQueryAgent</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "TallyBigQueryAgent");

    public static string ConfigPath  => Path.Combine(DataDir, "config.json");
    public static string DatabasePath => Path.Combine(DataDir, "agent.db");
    public static string QueueDir    => Path.Combine(DataDir, "queue");
    public static string LogsDir     => Path.Combine(DataDir, "Logs");
    public static string TriggerDir  => Path.Combine(DataDir, "triggers");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(QueueDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(TriggerDir);
    }
}
