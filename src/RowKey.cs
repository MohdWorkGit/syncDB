namespace SyncDb;

/// <summary>
/// A target business key (one or more column values) with value-based equality,
/// so it can be used as a dictionary key for per-row deduplication within a
/// batch — the role Python's tuple key plays in <c>plan_batch</c>.
/// </summary>
public sealed class RowKey(object?[] values) : IEquatable<RowKey>
{
    private readonly object?[] _values = values;

    /// <summary>The key column values, ordered like TARGET_KEY_COLUMNS.</summary>
    public IReadOnlyList<object?> Values => _values;

    public bool Equals(RowKey? other)
    {
        if (other is null || other._values.Length != _values.Length)
        {
            return false;
        }
        for (var i = 0; i < _values.Length; i++)
        {
            if (!Equals(_values[i], other._values[i]))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as RowKey);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var v in _values)
        {
            hash.Add(v);
        }
        return hash.ToHashCode();
    }
}
