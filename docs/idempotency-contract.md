# Idempotency contract

**Between:** the Tally BigQuery Agent (this repo) and the tally-ingestion-api.

The agent extracts from Tally and POSTs batches. It holds no cloud credentials
and never writes to BigQuery. Deduplication therefore happens in the ingestion
API — but the API can only deduplicate what the agent identifies, and this
document is what the agent promises to identify.

Repeated syncs writing duplicate rows is the defect this contract exists to
close.

---

## What the agent guarantees

1. **A deterministic batch id.** The same window, re-read, produces the same
   `batch_id`. A retried or repeated batch is recognisable as the same batch.
2. **A stable record key on every row**, in the `_record_key` field.
   Re-extracting the same source data produces the same key.
3. **No key is positional.** No key contains `line_index`, a sequence number, or
   an ordinal derived from Tally's response order.
4. **A key changes only when the record's business content changes.**

What the agent does **not** guarantee, and the API must handle:

- **Batches are not row-complete for a voucher.** Rows are sliced at
  `uploadBatchMaxRecords` (default 5000), so one voucher's lines can straddle
  two batches. **Do not implement "delete all rows for this voucher, then insert
  the batch"** — the second batch would delete the first batch's rows. MERGE on
  `_record_key`, row by row.
- **Ordering.** Batches may arrive out of order and may be retried.
- **Deletion.** A voucher deleted in Tally simply stops appearing. Detect it by
  diffing `voucher_guid_manifest`, not by absence from a batch.

---

## Fields on every record

| Field | Type | Meaning |
|---|---|---|
| `_record_key` | STRING (32 hex chars) | **The MERGE key.** Stable across re-reads. |
| `_company` | STRING | Tally company the row came from. Part of identity in practice — one agent, one company, but do not merge across companies. |
| `_sync_id` | STRING | The run that produced this copy. Audit only — **changes every upload**. |
| `_sync_timestamp` | STRING (ISO 8601 UTC) | When this copy was produced. Audit only — **changes every upload**. |

`_sync_id` and `_sync_timestamp` must never take part in a MERGE key. They differ
on every upload by construction, so including them turns MERGE back into append.

## Fields on every batch (HTTP POST)

| Field | Type | Meaning |
|---|---|---|
| `batch_id` | STRING | Deterministic. See below. |
| `dataset` | STRING | One of the 33 below. |
| `company` | STRING | Tally company name. |
| `window_from`, `window_to` | STRING (`yyyy-MM-dd`) or null | Date window. Null for masters. |
| `record_count` | INT | Rows in this batch. |
| `checksum_sha256` | STRING | SHA-256 of the gzipped payload — transport integrity. |
| `content_checksum` | STRING | SHA-256 of the rows **excluding audit fields** — identical business content gives an identical value even on a different run. |
| `schema_version` | STRING | Currently `1.0`. |

### How `batch_id` is built

```
{agent_id}-{company_id}-{dataset}-{window_from}-{window_to}-{sequence:D6}-{content_checksum[..12]}
```

Every input is stable except `sequence`, which is a per-dataset monotonic
counter. **Two batches with the same `batch_id` are the same batch** and the
second may be discarded. Two batches with the same `content_checksum` but a
different `sequence` are the same *content* re-extracted later — MERGE on
`_record_key` makes that a no-op.

---

## The key for every dataset

`_record_key` = SHA-256 of the dataset name, the columns below, and an
occurrence number, truncated to 32 hex characters.

The **occurrence number** disambiguates rows identical in every column below —
two identical freight allocations on one voucher, say. It is assigned over the
complete row set for a dataset and window, before batching. This is safe
because such rows are interchangeable: which copy gets occurrence 0 cannot
matter, and re-reading the same voucher yields the same multiset and so the same
set of keys.

| Dataset | Kind | Key columns | Notes |
|---|---|---|---|
| `companies` | Master | `company_name` | no GUID is fetched for Company |
| `cost_categories` | Master | `master_guid` |  |
| `cost_centres` | Master | `master_guid` |  |
| `currencies` | Master | `master_guid` |  |
| `godowns` | Master | `master_guid` |  |
| `groups` | Master | `master_guid` |  |
| `gst_rates` | Master | `master_guid` | one row per stock item |
| `ledgers` | Master | `master_guid` |  |
| `stock_groups` | Master | `master_guid` |  |
| `stock_items` | Master | `master_guid` |  |
| `stock_standard_costs` | Master | `master_guid` + `effective_date` |  |
| `stock_standard_prices` | Master | `master_guid` + `effective_date` |  |
| `uom` | Master | `master_guid` |  |
| `voucher_types` | Master | `master_guid` |  |
| `bank_allocations` | Voucher | `voucher_guid` + `ledger_name` + `instrument_number` + `bank_name` + `amount` |  |
| `bank_book` | Voucher | `bank_account` + `txn_date` + `voucher_number` + `cheque_number` + `debit` + `credit` | ⚠️ NO voucher GUID — see the contract document |
| `bill_allocations` | Voucher | `voucher_guid` + `ledger_name` + `bill_ref` + `bill_type` + `amount` |  |
| `cost_centre_allocations` | Voucher | `voucher_guid` + `ledger_name` + `cost_centre` + `cost_category` + `amount` |  |
| `day_book` | Voucher | `voucher_guid` + `ledger_name` + `amount` + `is_deemed_positive` |  |
| `inventory_entries` | Voucher | `voucher_guid` + `stock_item` + `godown` + `quantity` + `rate` + `amount` |  |
| `purchase_register` | Voucher | `guid` |  |
| `sales_invoice_lines` | Voucher | `voucher_guid` + `stock_item` + `godown` + `quantity` + `rate` + `amount` |  |
| `sales_register` | Voucher | `guid` |  |
| `voucher_guid_manifest` | Voucher | `guid` |  |
| `voucher_headers` | Voucher | `guid` |  |
| `voucher_lines` | Voucher | `voucher_guid` + `ledger_name` + `amount` + `is_deemed_positive` |  |
| `vouchers` | Voucher | `voucher_guid` + `ledger_name` + `amount` + `is_deemed_positive` | legacy copy of day_book; off by default |
| `balance_sheet` | Snapshot | `window_to` + `ledger_name` |  |
| `outstanding_payables` | Snapshot | `window_to` + `party_name` |  |
| `outstanding_receivables` | Snapshot | `window_to` + `party_name` |  |
| `profit_loss` | Snapshot | `window_to` + `ledger_name` |  |
| `stock_summary` | Snapshot | `window_to` + `item_name` |  |
| `trial_balance` | Snapshot | `window_to` + `ledger_name` |  |
⚠️ = no fully unique natural key; see below.

---

## Datasets needing a decision

### `bank_book` ⚠️

Tally's bank book rows carry **no voucher GUID**. The key falls back to
`bank_account` + `txn_date` + `voucher_number` + `cheque_number` + `debit` +
`credit`. Two identical same-day transfers on the same account with no cheque
number are indistinguishable and rely entirely on the occurrence number.

**Consequence:** if such a pair exists and Tally returns them in a different
order between runs, the two rows swap keys. The *set* is still correct and the
MERGE still converges — no duplicate, no loss — but an individual row's key is
not stable.

**The fix, if this matters:** carry `voucher_guid` on bank_book rows. It is
available at extraction time; it simply was not projected. That is an agent
change, deliberately not made here because `bank_book` is a derived convenience
view of data already keyed correctly in `voucher_lines` and `bank_allocations`.

### Snapshots — `trial_balance`, `balance_sheet`, `profit_loss`, `stock_summary`, `outstanding_payables`, `outstanding_receivables`

These are values **as of a date**, and the rows carry no as-of column. The key
therefore includes the batch's `window_to`.

**Consequence:** two snapshots taken on the same day are one record and the later
overwrites the earlier — correct. Snapshots on different days are different
records — also correct, and it preserves history.

**If the ingestion API ever changes `window_to` semantics for snapshots, this
key changes with it.** Flagging it because it is the only place a key depends on
batch metadata rather than row content.

### `companies`

Keyed on `company_name` because the Company collection fetch does not request a
GUID. Stable in practice — one company, renamed approximately never — but it is
a name, not an identifier.

---

## What the ingestion API should do

```sql
MERGE INTO `<target>` AS t
USING <staged batch> AS s
   ON t._record_key = s._record_key
  AND t._company    = s._company
  AND t.dataset     = s.dataset
WHEN MATCHED THEN UPDATE SET
      payload = s.payload, loaded_at = CURRENT_TIMESTAMP()
WHEN NOT MATCHED THEN INSERT ...
```

Row-by-row on `_record_key`. Not by voucher, not by window, not by batch — see
the "not guaranteed" note about batch boundaries above.

## Verifying it holds

Run the same sync twice; the raw row count must be identical after the second.
If it grows, either the API is appending, or a key is unstable — and the second
is testable without the cloud: re-extract the same window twice and compare the
`_record_key` sets. `RecordKeyTests` does exactly that, including the
line-renumbering case that caused 11,695 unbalanced vouchers.
