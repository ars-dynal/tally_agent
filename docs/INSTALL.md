# Build & Installation Runbook

## 1. Build the installer (developer machine, Windows)

Prerequisites:

* Windows 10/11 or Windows Server 2019+
* .NET 8 SDK — https://dotnet.microsoft.com/download/dotnet/8.0
* Inno Setup 6 — https://jrsoftware.org/isdl.php

```powershell
git clone <repo> tally-bigquery-agent
cd tally-bigquery-agent
dotnet test tests\TallyAgent.Core.Tests   # unit tests must pass first
.\build\build.ps1
# → dist\Tally BigQuery Agent Setup.exe
```

Optional signing (recommended for production):

```powershell
.\build\sign.ps1 -CertThumbprint <your-code-signing-cert-thumbprint>
```

## 2. Install on the Tally machine

1. Ensure TallyPrime is running and the XML server is on:
   **F1 → Settings → Connectivity → Client/Server configuration →
   "TallyPrime acts as" = Both (or Server), port 9000.**
2. Run `Tally BigQuery Agent Setup.exe` **as administrator**.
3. Fill the wizard pages:
   * Tally: host `127.0.0.1`, port `9000`, company name (or leave blank for
     auto-discovery), extraction start date, sync frequency (default 15 min),
     dataset toggles.
   * Cloud: ingestion API URL, Agent ID, Company ID, API token, environment.
   * Notifications: admin email, optional webhooks.
4. The installer tests the Tally and cloud connections, saves the configuration
   (token encrypted with Windows DPAPI), installs the **TallyBigQueryAgent**
   service, configures automatic start + failure recovery (1 min / 5 min / 15 min),
   and starts it immediately.
5. Close the installer — the service keeps running. Reboot-safe.

## 3. Verify

* `services.msc` → *Tally BigQuery Data Sync Agent* → Running, Startup type Automatic.
* Start Menu → **Tally BigQuery Agent Manager** → status dashboard (Tally test,
  cloud test, pending batches, recent errors).
* Logs: `C:\ProgramData\TallyBigQueryAgent\Logs\agent-YYYYMMDD.log` and Windows
  Event Viewer → Application → source `TallyBigQueryAgent`.
* Cloud dashboard: heartbeat visible within 5 minutes.

## 4. Day-2 operations

| Task | How |
|---|---|
| Change settings | Manager → Update Configuration → restart service |
| Force a sync | Manager → Sync Now (or `TallyAgent.Cli sync-now`) |
| Retry failed uploads | Manager → Retry Failed Batches |
| Diagnostics for support | Manager → Export Diagnostics (sanitised ZIP) |
| Upgrade | Run the newer Setup.exe — config/queue/logs preserved |
| Uninstall | Apps & Features → uninstall → choose keep/delete data |

## 5. CLI reference (`C:\Program Files\Tally BigQuery Agent\cli\TallyAgent.Cli.exe`)

```
test-tally [--host H] [--port P] [--company C] [--json]
test-cloud [--url U] [--token T] [--agent-id A] [--company-id C] [--environment E] [--json]
save-config --file plain.json | --set tally.host=127.0.0.1 [--set ...]
show-config          # secrets masked
sync-now
retry-failed
export-diag
status [--json]
protect <value>      # print DPAPI-encrypted form of a secret
```

## 6. Firewall / network

* Outbound HTTPS (443) to the ingestion API only. No inbound ports.
* Never expose Tally port 9000 beyond the LAN.

## 7. Troubleshooting quick table

| Symptom | Category in logs | Fix |
|---|---|---|
| "Nothing is listening on 127.0.0.1:9000" | TallyNotRunning | Start TallyPrime; enable XML server |
| "Company 'X' is not open" | TallyCompanyNotOpen | Open the company in Tally or fix the name |
| Uploads queueing, not sending | InternetUnavailable / CloudApiUnavailable | Check connectivity; queue drains automatically |
| "rejected the agent token" | AuthenticationFailure | Re-issue token; Manager → Update Configuration |
| Batches failed with schema errors | SchemaMismatch | Upgrade agent to the version matching the warehouse schema |
