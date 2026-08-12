# Tally Agent v2.0.1 runtime hardening

This hotfix is based on the first real v2.0.0 Force Full Sync validation on 2026-08-12.

## Fixed

- Adaptive voucher-window timeout splitting no longer crashes inside the logging statement.
- Force Full Sync resets the voucher checkpoint only once.
- SQLite uses a 15-second busy timeout and IMMEDIATE write transactions for dequeue/ack operations to avoid deferred read-to-write SQLITE_BUSY races.
- Tally XML scalar readers fall back to same-named master attributes (notably `NAME`), preventing valid Group and StockItem masters from being discarded when Tally emits names as attributes.
- Financial/stock report parsing now searches nested report nodes rather than assuming `DSPACCNAME` is a direct root child.
- Full sync performs conservative cross-dataset validation and will not silently certify contradictory zero-row snapshots as a clean success.
- Missing `/heartbeat` is treated as a degraded monitoring capability rather than cloud-offline; retries back off to hourly while `/health` remains available.
- Daily health summaries are generated once per day (default 08:00 server local time) and sent to configured direct webhooks plus the cloud notification endpoint.
- Daily cloud summaries carry `recipient_email` for server-side email fan-out without storing SMTP credentials in the Windows agent.

## Cloud-side dependency

The deployed Cloud Run API must implement `/heartbeat` and `/errors` (or compatible routes) before remote heartbeat history and email fan-out are fully operational. The Windows agent continues to upload `/sync` batches while those optional monitoring routes are unavailable.

## Validation required before production approval

1. CI build and tests pass.
2. Install v2.0.1 over v2.0.0 without deleting `agent.db` or GCS objects.
3. Verify Tally and Cloud connection tests.
4. Trigger Force Full Sync once from the configured historical start date.
5. Confirm timed-out large windows split and continue.
6. Verify Groups, Stock Items, Trial Balance, Balance Sheet, P&L and Stock Summary record counts.
7. Confirm no failed/pending batches remain after the full walk.
