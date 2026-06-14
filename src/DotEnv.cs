namespace SyncDb;

/// <summary>
/// Minimal <c>.env</c> loader, the equivalent of python-dotenv's
/// <c>load_dotenv()</c>: read <c>KEY=VALUE</c> lines from a file and set any
/// that are not already present in the process environment. Existing
/// environment variables always win, so real env vars override the file.
/// </summary>
internal static class DotEnv
{
    public static void Load(string path = ".env")
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();

            var value = line[(eq + 1)..].Trim();
            // Strip an inline comment on unquoted values (mirrors how the
            // shipped .env.example annotates entries: VALUE   # explanation).
            if (value.Length > 0 && value[0] is not '"' and not '\'')
            {
                var hash = value.IndexOf('#');
                if (hash >= 0)
                {
                    value = value[..hash].Trim();
                }
            }
            else if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
