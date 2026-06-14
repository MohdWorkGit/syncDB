using System.Globalization;
using Microsoft.Extensions.Logging;
using SyncDb;

// Entry point: throttled copy of a large Oracle change-log table into a
// reshaped target.
//
//   cp .env.example .env   # then edit credentials / table names / pacing
//   dotnet run -- [--batch-size N] [--sleep SECONDS] [--restart]

int? batchSize = null;
double? sleepSeconds = null;
var restart = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--batch-size":
            batchSize = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--sleep":
            sleepSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--restart":
            restart = true;
            break;
        case "-h" or "--help":
            Console.WriteLine(
                "Usage: dotnet run -- [--batch-size N] [--sleep SECONDS] [--restart]\n" +
                "  --batch-size N   rows per cycle (overrides BATCH_SIZE)\n" +
                "  --sleep SECONDS  seconds between cycles (overrides SLEEP_SECONDS)\n" +
                "  --restart        ignore any existing checkpoint and start over");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

DotEnv.Load();

SyncConfig config;
try
{
    config = SyncConfig.FromEnvironment();
}
catch (ConfigException exc)
{
    Console.Error.WriteLine(exc.Message);
    return 2;
}

if (batchSize is { } bs)
{
    config = config with { BatchSize = bs };
}
if (sleepSeconds is { } ss)
{
    config = config with { SleepSeconds = ss };
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(ParseLevel(config.LogLevel))
        .AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
});

var log = loggerFactory.CreateLogger("syncdb");

var sync = new TableSync(config, loggerFactory);
if (restart)
{
    sync.Checkpoint.Clear();
}

try
{
    sync.Run();
}
catch (Exception exc)
{
    log.LogError(exc, "Sync aborted");
    return 1;
}

return 0;

static LogLevel ParseLevel(string level) => level.Trim().ToUpperInvariant() switch
{
    "DEBUG" => LogLevel.Debug,
    "INFO" or "INFORMATION" => LogLevel.Information,
    "WARNING" or "WARN" => LogLevel.Warning,
    "ERROR" => LogLevel.Error,
    "CRITICAL" => LogLevel.Critical,
    "NONE" => LogLevel.None,
    _ => LogLevel.Information,
};
