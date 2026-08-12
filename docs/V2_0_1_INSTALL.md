# v2.0.1 upgrade procedure

1. Stop the existing Tally BigQuery Agent service.
2. Do not uninstall v2.0.0, delete `agent.db`, clear the queue, or delete GCS objects.
3. Run the v2.0.1 installer as Administrator and upgrade the existing installation.
4. Open the Manager and confirm version `2.0.1`.
5. Verify the existing configuration, especially Tally host/port/company, extraction start date, 240-minute frequency, cloud URL, agent/company IDs and Production environment.
6. Test Tally connection and Cloud connection; both must pass.
7. Start the service.
8. Trigger **Force Full Sync** exactly once.
9. Keep Tally available and Windows awake while the initial history walk runs.
10. Validate logs and GCS before accepting the baseline. A large voucher window may time out, but the log should show it split into smaller windows and continue.
11. Do not enable downstream BigQuery baseline loading until full sync is successful, pending/failed batches are zero, and key dataset counts are validated.
