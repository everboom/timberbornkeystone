# tools

Repo-local helper scripts. Not part of the build; not deployed.

## `generate-flourish-blueprints.py`

Pseudo-random generator for Keystone flourish blueprints — mini-variant
compositions of 2-3 vanilla flora meshes per tile, used at L1/L2/L3 of any
biome (e.g. `KeystoneRiparianMini1..5`).

**When to use:** authoring three or more flourish blueprints for any biome.
Hand-writing the JSON is the antipattern -- the script encodes positional
constraints (center-origin tile coords, ≥0.20 plant spacing, COG offset
window, position range to avoid bbox overhang, no rotations, lifecycle phase
wiring to `<stem>Seedling[Dry|Dead]`) that hand-authoring has repeatedly
violated, costing many rounds of in-game iteration to debug.

```bash
# 10 Riparian-style minis: cattail + sunflower + at most one cassava per blueprint
python tools/generate-flourish-blueprints.py \
    --prefix KeystoneRiparianMini \
    --plants Cattail Sunflower Cassava:1 \
    --count 10 --seed 42

# 20 grassland-style minis using mature meshes
python tools/generate-flourish-blueprints.py \
    --prefix KeystoneGrasslandMini \
    --plants Sunflower Dandelion \
    --count 20 --stage Mature
```

`--plants Name:K` caps a plant at K instances per blueprint. Default cap
comes from `--plants-per-blueprint` (default 3). `--seed` makes runs
reproducible. `--stage Seedling|Mature` picks the mesh stage. `--force`
overwrites existing files. See `python tools/generate-flourish-blueprints.py --help`
for the full surface.

The mesh catalog (which plant names are recognized and what mesh paths they
resolve to) is the `CATALOG` dict at the top of the script. To add a new
vanilla mesh: look it up in `dump/mesh-paths.csv` and add an entry to
`CATALOG`. Tunable constraints (`MIN_PLANT_DISTANCE`, `MIN_COG_OFFSET`,
`MAX_COG_OFFSET`, `POSITION_RANGE`) live just below the catalog.

**Conventions baked in** -- if you find yourself wanting to override these,
consider whether the design has actually shifted, or whether the script's
parameters can express what you need:

- Center-origin tile coordinates, range roughly `[-0.5, +0.5]`.
- Plant positions stay in `[-0.40, +0.40]` to avoid mesh bounding-box
  overhang.
- Plants ≥0.20 apart from each other.
- Cluster centre of gravity 0.10–0.25 from origin (asymmetric, not extreme).
- Per-plant scale uniform-random in `[0.70, 1.00]` (uniform X/Y/Z; same
  scale across the plant's three lifecycle phases so it doesn't visibly
  resize when wilting).
- Rotations stay at `(0, 0, 0)` — vanilla mesh widths make rotation
  visually unsafe.
- Standard spec set (Watered/Floodable/Demolishable + KeystoneFlourishSpec
  + KeystoneVariantSpec) plus three lifecycle leaves
  (`#Alive` / `#Dying` / `#Dead`).

See project memory entries `feedback_transformspec_pivot.md`,
`feedback_no_transformspec_rotations.md`, and
`feedback_use_blueprint_generator_script.md` for the history of how each
convention was established.

## `copy-player-log.ps1`

Copies Timberborn's `Player.log` and `Player-prev.log` from
`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\` into the repo's
`dump/` folder. Run after a crash or unexpected behavior so the latest
log is available at a stable repo-relative path
(`dump/Player.log` / `dump/Player-prev.log`) for inspection. `dump/` is
gitignored.

```powershell
.\tools\copy-player-log.ps1
```

`Player.log` is the current/last run; `Player-prev.log` is the run
before that (rotated when the game starts). After a hard crash where
you've already restarted to test a fix, the original traceback lives
in `Player-prev.log`.

## `find-workshop-mod.ps1`

Finds a Timberborn mod in the local Steam Workshop folder by name or id.
Reads each mod's `manifest.json` to extract its `Id`, `Name`, and `Version`.

```powershell
# Search by name or id (case-insensitive substring match):
.\tools\find-workshop-mod.ps1 chronicle

# List all installed workshop mods:
.\tools\find-workshop-mod.ps1

# Show full folder paths instead of Steam item ids:
.\tools\find-workshop-mod.ps1 -Search moddable -Full
```

Workshop directory is resolved in order: `-WorkshopDir` parameter,
then the `KEYSTONE_WORKSHOP_DIR` environment variable. If neither is
set, the script errors and asks you to provide one.

Tolerates sloppy JSON in manifests (trailing commas, line/block comments)
which some workshop mods ship.

## `generate-api-cache.ps1`

Reflects over every `Timberborn.*.dll` in a Timberborn install and writes a
tiered API reference to `docs/timberborn-api-full.md`.

```powershell
# Auto-resolves from KEYSTONE_TIMBERBORN_DIR if set:
.\tools\generate-api-cache.ps1

# Or pass the install root (or the Managed dir) explicitly:
.\tools\generate-api-cache.ps1 -ManagedDir '<Timberborn install>'

# Custom output:
.\tools\generate-api-cache.ps1 -OutFile 'C:\tmp\api.md'
```

**What "tiered" means.** Every public, non-nested type appears in the per-DLL
type index with a kind marker (`[I]` interface, `[E]` enum, `[S]` struct,
`[A]` abstract class, `[X]` static class, `[C]` class). Types in **Tier A**
get full member signatures dumped underneath the index; everything else is
name-only. Tier A is all interfaces plus concrete types whose name ends in
one of: `Service`, `System`, `Tracker`, `Manager`, `Registry`, `Provider`,
`Spawner`, `Highlighter`, `Marker`, `Renderer`, `Drawer`, `Tool`, `Factory`,
`Pool`, `Mediator`, `Hub`, `Configurator`, `Spec`.

**When to re-run:** after a Timberborn version bump, or when investigating
a system the current cache doesn't cover. The script is deterministic on
input — same DLLs, same output — so re-runs produce git-reviewable diffs.

**Why PowerShell, not C#.** The generator runs on Windows PowerShell 5.1
(.NET Framework 4.8). Some Timberborn assemblies use C# default-interface-
methods, which 4.8's reflection rejects in `GetExportedTypes()`. The script
falls back to `GetTypes()` and salvages from `ReflectionTypeLoadException.Types`,
recovering ~25 DLLs that would otherwise be dropped. If we ever need a
reflection feature 4.8 truly can't provide, port to a small `dotnet`
console app under `tools/` and have the script invoke it.

**Why em-dashes are avoided in this script's source.** PowerShell 5.1 reads
unmarked UTF-8 source as ANSI, mangling non-ASCII characters into parse
errors. Stick to ASCII inside `.ps1` files, or save with a UTF-8 BOM.

## `new-bug-report.ps1`

Files a structured bug-report **issue** on the GitHub repo via `gh issue create`.
Run from inside the repo; requires the GitHub CLI authenticated (`gh auth login`).

```powershell
.\tools\new-bug-report.ps1 -Title "Cattails flicker at L3" `
    -GameVersion 1.0.0.0 -Faction Folktails `
    -Description "Cattail flourishes z-fight on river edges at level 3." -IncludeLog
```

Assembles a body with Timberborn version, faction, description, and a Player.log
pointer, and labels the issue `bug`. `-IncludeLog` copies the current Player.log
into `dump/` (via `copy-player-log.ps1`) so you can drag it onto the issue in the
browser -- gh can't upload attachments itself. Omit `-Description` to be prompted.

## `new-idea.ps1`

Posts a feature idea to GitHub **Discussions** (the `Ideas` category by default).
There is no native `gh discussion` command, so this wraps the GraphQL
`createDiscussion` mutation: it resolves the repo node ID and category ID
automatically (via GraphQL variables, which sidestep PowerShell's native-arg
quoting trap), creates the discussion, and prints its URL. Requires `gh`
authenticated.

```powershell
.\tools\new-idea.ps1 -Title "Sea and ocean biomes" `
    -Body "Large-water biomes with their own fauna -- whales, manta rays, ocean life."
```

`-Category <name>` targets a different Discussions category (case-insensitive;
defaults to `Ideas`). Omit `-Body` to be prompted. Use this to seed or grow the
Ideas board from the CLI instead of the web UI.

**Bug reports vs. ideas.** Bugs are concrete and trackable, so they go to
**Issues** (`new-bug-report.ps1`); open-ended suggestions that benefit from
community upvoting go to **Discussions** (`new-idea.ps1`). The README's
"Suggest a feature or report a bug" section points players to the same split.
