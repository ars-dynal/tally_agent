using TallyAgent.Core.Configuration;

namespace TallyAgent.Core.Tally.Extractors;

public enum DatasetKind
{
    Master,       // full re-extract each cycle (cheap collections)
    Voucher,      // date-windowed via Day Book fan-out
    Snapshot,     // date-ranged report snapshot (TB, BS, P&L, stock summary, outstanding)
}

public sealed record DatasetDefinition(string Name, DatasetKind Kind, string BigQueryTable);

/// <summary>The 33 datasets from Tally_Schema_Design.xlsx, with enable-flag filtering.</summary>
public static class DatasetRegistry
{
    public static readonly IReadOnlyList<DatasetDefinition> All =
    [
        // Masters
        new("companies",              DatasetKind.Master,   "tally_companies"),
        new("groups",                 DatasetKind.Master,   "tally_groups"),
        new("ledgers",                DatasetKind.Master,   "tally_ledgers"),
        new("voucher_types",          DatasetKind.Master,   "tally_voucher_types"),
        new("cost_centres",           DatasetKind.Master,   "tally_cost_centres"),
        new("cost_categories",        DatasetKind.Master,   "tally_cost_categories"),
        new("currencies",             DatasetKind.Master,   "tally_currencies"),
        new("uom",                    DatasetKind.Master,   "tally_uom"),
        new("gst_rates",              DatasetKind.Master,   "tally_gst_rates"),
        new("opening_bills",          DatasetKind.Master,   "tally_opening_bills"),
        // Inventory masters
        new("stock_groups",           DatasetKind.Master,   "tally_stock_groups"),
        new("stock_items",            DatasetKind.Master,   "tally_stock_items"),
        new("godowns",                DatasetKind.Master,   "tally_godowns"),
        new("stock_standard_costs",   DatasetKind.Master,   "tally_stock_standard_costs"),
        new("stock_standard_prices",  DatasetKind.Master,   "tally_stock_standard_prices"),
        // Voucher-derived (Day Book fan-out)
        new("vouchers",               DatasetKind.Voucher,  "tally_vouchers"),
        new("voucher_headers",        DatasetKind.Voucher,  "tally_voucher_headers"),
        new("voucher_lines",          DatasetKind.Voucher,  "tally_voucher_lines"),
        new("bill_allocations",       DatasetKind.Voucher,  "tally_bill_allocations"),
        new("bank_allocations",       DatasetKind.Voucher,  "tally_bank_allocations"),
        new("cost_centre_allocations",DatasetKind.Voucher,  "tally_cost_centre_allocations"),
        new("inventory_entries",      DatasetKind.Voucher,  "tally_inventory_entries"),
        new("day_book",               DatasetKind.Voucher,  "tally_day_book"),
        new("bank_book",              DatasetKind.Voucher,  "tally_bank_book"),
        new("sales_register",         DatasetKind.Voucher,  "tally_sales_register"),
        new("purchase_register",      DatasetKind.Voucher,  "tally_purchase_register"),
        new("sales_invoice_lines",    DatasetKind.Voucher,  "tally_sales_invoice_lines"),
        // Snapshot reports
        new("trial_balance",          DatasetKind.Snapshot, "tally_trial_balance"),
        new("balance_sheet",          DatasetKind.Snapshot, "tally_balance_sheet"),
        new("profit_loss",            DatasetKind.Snapshot, "tally_profit_loss"),
        new("stock_summary",          DatasetKind.Snapshot, "tally_stock_summary"),
        new("outstanding_payables",   DatasetKind.Snapshot, "tally_outstanding_payables"),
        new("outstanding_receivables",DatasetKind.Snapshot, "tally_outstanding_receivables"),
    ];

    private static readonly HashSet<string> InventoryDatasets =
        ["stock_groups","stock_items","godowns","stock_standard_costs","stock_standard_prices",
         "inventory_entries","stock_summary"];

    private static readonly HashSet<string> GstDatasets =
        ["gst_rates","sales_register","purchase_register","sales_invoice_lines"];

    private static readonly HashSet<string> CostCentreDatasets =
        ["cost_centres","cost_categories","cost_centre_allocations"];

    /// <summary>Datasets enabled by the config toggles.</summary>
    public static IReadOnlyList<DatasetDefinition> Enabled(TallySettings s) =>
        All.Where(d => IsEnabled(d, s)).ToList();

    private static bool IsEnabled(DatasetDefinition d, TallySettings s)
    {
        if (!s.EnableInventory && InventoryDatasets.Contains(d.Name)) return false;
        if (!s.EnableGst && GstDatasets.Contains(d.Name)) return false;
        if (!s.EnableCostCentres && CostCentreDatasets.Contains(d.Name)) return false;
        return d.Kind switch
        {
            DatasetKind.Master => s.EnableMasters,
            DatasetKind.Voucher => s.EnableVouchers,
            DatasetKind.Snapshot => true,
            _ => true,
        };
    }
}
