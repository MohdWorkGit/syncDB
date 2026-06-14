using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SyncDb;

/// <summary>
/// Checkpoint persistence so an interrupted sync resumes where it stopped.
///
/// The checkpoint stores the last successfully committed key value. Writes are
/// atomic (write to a temp file, then rename) so a crash mid-write cannot leave
/// a corrupt checkpoint behind.
/// </summary>
public sealed class Checkpoint(string path, ILogger logger)
{
    /// <summary>Return the last committed key, or null if starting fresh.</summary>
    public object? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var lastKey = root.TryGetProperty("last_key", out var lk)
            ? JsonValueToClr(lk)
            : null;
        var rows = root.TryGetProperty("rows_processed", out var rp) ? rp.ToString() : "?";
        logger.LogInformation(
            "Resuming from checkpoint: last_key={LastKey} ({Rows} rows done so far)",
            lastKey, rows);
        return lastKey;
    }

    public void Save(object? lastKey, long rowsProcessed)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["last_key"] = lastKey,
            ["rows_processed"] = rowsProcessed,
        });

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(directory, $".ckpt-{Path.GetRandomFileName()}");
        try
        {
            File.WriteAllText(tmp, json);
            // Atomic replace on the same volume (mirrors Python's os.replace).
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
            throw;
        }
    }

    public void Clear()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            logger.LogInformation("Sync finished; checkpoint file removed.");
        }
    }

    /// <summary>
    /// Convert a JSON scalar back to a CLR value suitable for binding as the
    /// paging key. Numbers become <see cref="decimal"/> (covers Oracle NUMBER
    /// keys); everything else stays a string.
    /// </summary>
    private static object? JsonValueToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => e.ToString(),
    };
}
