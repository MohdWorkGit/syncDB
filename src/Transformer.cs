using System.Globalization;

namespace SyncDb;

/// <summary>
/// Row transformation: maps a source change-log row to the target structure.
///
/// This class is the one you edit to adapt the project to your own tables.
///
/// The source is a <em>change log</em>: every row describes one change to apply
/// to the target — an insert, an update, or a delete — identified by an
/// operation column. The shipped example reads ORDER_CHANGES and maintains a
/// flattened ORDER_SUMMARY reporting table (see sql/example_tables.sql):
///
///   - first/last name are merged into a single CUSTOMER_NAME
///   - the order date is split into ORDER_YEAR / ORDER_MONTH
///   - the total is computed from UNIT_PRICE * QUANTITY
///   - 2-letter status codes are decoded into readable values
///   - country codes are bucketed into regions
///
/// Delete rows usually carry only the business key (other columns NULL), which
/// is why key extraction (<see cref="TransformKey"/>) is separate from full
/// transformation (<see cref="Transform"/>).
/// </summary>
public static class Transformer
{
    // Columns fetched from the source change-log table. Must include the paging
    // key (KEY_COLUMN, e.g. CHANGE_ID) and the operation column (OP_COLUMN).
    public static readonly string[] SourceColumns =
    [
        "CHANGE_ID",
        "OPERATION",
        "ORDER_ID",
        "CUST_FIRST_NAME",
        "CUST_LAST_NAME",
        "ORDER_DATE",
        "UNIT_PRICE",
        "QUANTITY",
        "STATUS_CODE",
        "COUNTRY_CODE",
    ];

    // Columns written to the target table, in the order produced by Transform().
    public static readonly string[] TargetColumns =
    [
        "ORDER_ID",
        "CUSTOMER_NAME",
        "ORDER_YEAR",
        "ORDER_MONTH",
        "TOTAL_AMOUNT",
        "STATUS",
        "REGION",
        "SYNCED_AT",
    ];

    // Column(s) that uniquely identify a row in the TARGET table. Used to match
    // rows for MERGE (insert/update) and DELETE, and to deduplicate changes to
    // the same row within a batch. Must be a subset of TargetColumns.
    public static readonly string[] TargetKeyColumns = ["ORDER_ID"];

    private static readonly Dictionary<string, string> StatusMap = new()
    {
        ["NW"] = "NEW",
        ["SH"] = "SHIPPED",
        ["DL"] = "DELIVERED",
        ["CN"] = "CANCELLED",
    };

    private static readonly Dictionary<string, string> RegionMap = new()
    {
        ["US"] = "AMERICAS", ["CA"] = "AMERICAS", ["BR"] = "AMERICAS", ["MX"] = "AMERICAS",
        ["GB"] = "EMEA", ["DE"] = "EMEA", ["FR"] = "EMEA", ["AE"] = "EMEA", ["SA"] = "EMEA",
        ["IN"] = "APAC", ["JP"] = "APAC", ["CN"] = "APAC", ["AU"] = "APAC",
    };

    /// <summary>Extract the target business key from a source row (any operation).</summary>
    public static RowKey TransformKey(Row row) => new([row["ORDER_ID"]]);

    /// <summary>
    /// Turn one insert/update source row into a target tuple.
    ///
    /// The returned array is ordered like <see cref="TargetColumns"/>, which
    /// keeps the array-bound MERGE fast. Throw <see cref="TransformException"/>
    /// to have the sync engine log and skip a bad row. Never called for delete
    /// rows — those only need <see cref="TransformKey"/>.
    /// </summary>
    public static object?[] Transform(Row row)
    {
        var first = (row.Str("CUST_FIRST_NAME") ?? string.Empty).Trim();
        var last = (row.Str("CUST_LAST_NAME") ?? string.Empty).Trim();
        var parts = new[] { first, last }.Where(p => p.Length > 0);
        var customerName = string.Join(" ", parts);
        if (customerName.Length == 0)
        {
            customerName = "UNKNOWN";
        }

        var orderDate = row.Date("ORDER_DATE")
            ?? throw new TransformException($"ORDER_ID={row["ORDER_ID"]}: ORDER_DATE is NULL");

        var unitPrice = row.Num("UNIT_PRICE") ?? 0m;
        var quantity = row.Num("QUANTITY") ?? 0m;

        return
        [
            row["ORDER_ID"],
            customerName,
            orderDate.Year,
            orderDate.Month,
            Math.Round(unitPrice * quantity, 2, MidpointRounding.ToEven),
            StatusMap.GetValueOrDefault(row.Str("STATUS_CODE") ?? string.Empty, "UNKNOWN"),
            RegionMap.GetValueOrDefault(row.Str("COUNTRY_CODE") ?? string.Empty, "OTHER"),
            DateTime.Now,
        ];
    }
}

/// <summary>
/// A source change-log row: raw column values keyed by name, with typed
/// accessors. The .NET counterpart of the <c>dict(zip(SOURCE_COLUMNS, raw))</c>
/// the Python engine hands to the transformer.
/// </summary>
public readonly struct Row(IReadOnlyDictionary<string, int> columnIndex, object?[] values)
{
    public object? this[string column] => values[columnIndex[column]];

    public string? Str(string column) => this[column] switch
    {
        null => null,
        string s => s,
        var v => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    public decimal? Num(string column) => this[column] switch
    {
        null => null,
        decimal d => d,
        var v => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
    };

    public DateTime? Date(string column) => this[column] switch
    {
        null => null,
        DateTime dt => dt,
        var v => Convert.ToDateTime(v, CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Raised by the transformer for a row that should be logged and skipped
/// rather than aborting the run — the equivalent of Python's <c>ValueError</c>.
/// </summary>
public sealed class TransformException(string message) : Exception(message);
