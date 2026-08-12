# Cloud notification requirements for agent v2.0.1

The Windows agent intentionally does not store SMTP or Gmail credentials. Remote email health reporting must be implemented by the cloud notification service.

Required authenticated routes on the same base URL used by `/health` and `/sync`:

- `POST /heartbeat` — store latest agent health and optional heartbeat history.
- `POST /errors` — store error/health summaries and fan out notifications.

For a request with `is_summary=true`, `category=DailyHealth`, and a non-empty `recipient_email`, the cloud service should send one email summary to that recipient. It should deduplicate by `(agent_id, category, local/report date)` so retries cannot create duplicate daily emails.

Both routes should accept `X-API-Token`, `X-Agent-Id`, and `X-Environment` consistently with `/sync` and return a 2xx JSON response. A missing optional route must not affect `/sync` ingestion.
