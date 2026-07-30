using System.Text.Json;
using TallyAgent.Core.Security;

namespace TallyAgent.Core.Configuration;

/// <summary>
/// Loads and saves AgentConfig at %ProgramData%\TallyBigQueryAgent\config.json.
/// On save, secret fields are DPAPI-encrypted; on load they stay encrypted —
/// call the Get*Secret helpers to decrypt on demand so plaintext never sits
/// in long-lived state.
/// Writes are atomic (temp file + File.Replace) to survive power loss.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly string _path;

    public ConfigStore(string? path = null) => _path = path ?? AgentInfo.ConfigPath;

    public bool Exists => File.Exists(_path);

    public AgentConfig Load()
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException(
                $"Agent configuration not found at {_path}. Run the installer or 'TallyAgent.Cli save-config'.", _path);

        var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(_path), JsonOpts)
                  ?? throw new InvalidDataException($"Configuration at {_path} is empty or invalid JSON.");
        ConfigValidator.Validate(cfg);
        return cfg;
    }

    public AgentConfig LoadOrDefault() => Exists ? Load() : new AgentConfig();

    /// <summary>Encrypt secrets and atomically persist.</summary>
    public void Save(AgentConfig cfg)
    {
        ConfigValidator.Validate(cfg);
        cfg.Cloud.ApiToken = DpapiProtector.Protect(cfg.Cloud.ApiToken);
        cfg.Notifications.ErrorWebhookUrl = DpapiProtector.Protect(cfg.Notifications.ErrorWebhookUrl);
        cfg.Notifications.GoogleChatWebhookUrl = DpapiProtector.Protect(cfg.Notifications.GoogleChatWebhookUrl);
        cfg.Notifications.SlackWebhookUrl = DpapiProtector.Protect(cfg.Notifications.SlackWebhookUrl);

        AgentInfo.EnsureDirectories();
        var json = JsonSerializer.Serialize(cfg, JsonOpts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak");
        else File.Move(tmp, _path);
    }

    // ── decrypted secret accessors ────────────────────────────────
    public static string GetApiToken(AgentConfig c) => DpapiProtector.Unprotect(c.Cloud.ApiToken);
    public static string GetErrorWebhook(AgentConfig c) => DpapiProtector.Unprotect(c.Notifications.ErrorWebhookUrl);
    public static string GetGoogleChatWebhook(AgentConfig c) => DpapiProtector.Unprotect(c.Notifications.GoogleChatWebhookUrl);
    public static string GetSlackWebhook(AgentConfig c) => DpapiProtector.Unprotect(c.Notifications.SlackWebhookUrl);
}
