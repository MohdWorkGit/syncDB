# Deploying syncDB to an air-gapped machine

This guide builds and runs syncDB on a machine with **no internet access**. The
idea is simple: on a connected machine you gather the whole dependency closure
into one folder (the *bundle*), carry it across, and build/run entirely offline.

syncDB is a single .NET project (`SyncDb.csproj`) with **no frontend**, so the
only toolchain you need is the .NET 10 SDK. ODP.NET runs in fully-managed mode,
so **no Oracle Instant Client install is required** — but the target Oracle
database (your `ORACLE_DSN`) must still be reachable on the network from the
air-gapped machine.

There are two parts:

- **Part A** — on an internet-connected machine: build the bundle.
- **Part B** — on the air-gapped machine: restore, build, configure, run.

---

## Part A — On an internet-connected machine

### A1. Prerequisites

- Windows with PowerShell 5.1+ (the script targets Windows PowerShell).
- The **.NET 10 SDK** on `PATH` (`dotnet --version` should print `10.x`).
- A clone/copy of this repository.

### A2. Run the bundle script

From the repo root:

```powershell
# Minimal: just the NuGet dependency closure (you build on the target).
./scripts/prepare-offline-bundle.ps1

# Recommended for a true air-gap: also include a ready-to-run self-contained
# build and the .NET SDK installer.
./scripts/prepare-offline-bundle.ps1 -IncludeBuild -IncludeInstallers -Clean
```

Useful switches:

| Switch | Effect |
|--------|--------|
| `-OutDir <path>` | Output folder (default `offline-bundle/`, gitignored). |
| `-Runtime <rid>` | Runtime for the self-contained publish (default `win-x64`; e.g. `linux-x64`). |
| `-IncludeBuild` | Also produce `publish/` — a self-contained `syncdb` that runs without the SDK. |
| `-IncludeInstallers` | Also download the .NET 10 SDK offline installer into `installers/`. |
| `-Clean` | Delete the output folder before starting. |

### A3. What the bundle contains

```
offline-bundle/
  nuget-packages/        NuGet dependency closure (offline restore source)
  nuget.config.template  Drop next to SyncDb.csproj on the target (see B3a)
  publish/               (-IncludeBuild) self-contained syncdb.exe, ready to run
  installers/            (-IncludeInstallers) .NET 10 SDK offline installer
  MANIFEST.txt           Generated summary + quick usage
```

> **Note on the SDK installer URL.** It is point-in-time (pinned to the SDK
> version used at build time). If `-IncludeInstallers` fails to download, the
> script prints the URL so you can fetch it manually, or bring your own copy of
> the .NET 10 SDK installer.

### A4. Verify the bundle is self-sufficient (strongly recommended)

Before you carry it across, prove it works **with networking off**:

1. Disable the network adapter (or pull the cable / turn off Wi-Fi).
2. In a throwaway copy of the repo, restore and build against the bundle only:
   ```powershell
   dotnet restore SyncDb.csproj --packages <bundle>\nuget-packages
   dotnet build   SyncDb.csproj -c Release
   ```
   It must complete without reaching the network. If it tries to hit nuget.org,
   the closure is incomplete — re-run Part A (with `-Clean`) on the connected
   machine and check for any private/preview package sources.
3. Re-enable networking.

### A5. Copy across

Copy the entire `offline-bundle/` folder to the air-gapped machine by your
approved transfer method (USB, one-way diode, etc.).

---

## Part B — On the air-gapped machine

### B1. Install the toolchain

Install the **.NET 10 SDK** from `installers/` (or your own copy).

> If you only need to *run* syncDB (not edit/rebuild it), the `publish/` folder
> from `-IncludeBuild` is fully self-contained — skip to **B5** and run
> `publish\syncdb.exe`. No SDK install needed.

### B2. Place the bundle

Copy the bundle somewhere stable, e.g. `C:\offline-bundle`. Avoid paths with
spaces if you can — it keeps the commands below simpler.

### B3. Restore NuGet offline (choose ONE)

**a) Via nuget.config (per-repo, repeatable):**

1. Copy `nuget.config.template` next to `SyncDb.csproj` and rename it to
   `nuget.config`.
2. Replace every `OFFLINE_BUNDLE_PATH` with the bundle's full path, e.g.
   `C:/offline-bundle` (forward slashes are fine).
3. Restore:
   ```powershell
   dotnet restore SyncDb.csproj
   ```

**b) Via a one-off flag (no config file):**

```powershell
dotnet restore SyncDb.csproj --packages C:\offline-bundle\nuget-packages
```

Either way, restore must succeed without network access.

### B4. Build

```powershell
dotnet build SyncDb.csproj -c Release
```

For a self-contained executable you can copy/run elsewhere on the target:

```powershell
dotnet publish SyncDb.csproj -c Release -r win-x64 --self-contained `
    --packages C:\offline-bundle\nuget-packages -o publish
```

### B5. Configure and run

```powershell
copy .env.example .env
# Edit .env: ORACLE_USER / ORACLE_PASSWORD / ORACLE_DSN, table names, pacing,
# and any optional features (LINK_TABLE, PROCESSED_COLUMN, HISTORY_TABLE,
# QUARANTINE_TABLE). See README.md for the full configuration reference.

dotnet run                 # from source
#   - or -
publish\syncdb.exe         # the self-contained build
```

If you are setting up the Oracle schema for the first time, run
[`sql/example_tables.sql`](sql/example_tables.sql) (or your real schema) against
the target database first.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| `dotnet restore` tries to reach nuget.org | A `nuget.config` higher up the tree (or a machine-wide one) still lists an online source. Use option **B3a** with `<clear />` (the template already clears sources), or pass `--packages` and `-source <bundle>\nuget-packages`. |
| Restore can't find a package | The closure was built for a different `-Runtime`. Re-run Part A with the matching `-Runtime`, or restore on the target for the same RID you bundled. |
| `'dotnet' is not recognized` | The SDK isn't installed or not on `PATH`. Install from `installers/`, then open a new terminal. |
| Runs but can't connect to Oracle | This is a *network/credentials* issue, not a bundle issue. The DB host in `ORACLE_DSN` must be reachable from the air-gapped machine; verify host/port/service and that the firewall allows it. ODP.NET managed mode needs no Instant Client. |
| `Missing required environment variables` at startup | `.env` is absent or incomplete. Copy `.env.example` to `.env` and fill in `ORACLE_USER` / `ORACLE_PASSWORD` / `ORACLE_DSN`. |

See [README.md](README.md) for what syncDB does and the full configuration
reference, and [docs/workflow.md](docs/workflow.md) for its runtime behavior.
