# tools

Repo-local helper scripts. Mostly not part of the build; the exception is
`validate-localizations.ps1`, which the Mod build invokes (see below).

## `validate-localizations.ps1`

Quote-aware validator for the localization CSVs under
`unity-assets/Keystone/Data/Localizations/`. Every data row must have exactly
three fields (`ID,Text,Comment`) and a non-empty `ID`.

**Why it exists:** Timberborn loads localizations with LINQtoCSV, which throws
an `AggregatedException` ("reading data using type `LocalizationRecord`") and
fails the whole language load — i.e. a black-screen crash at game start — if
any row is malformed. The recurring mistake is an **unquoted comma** in the
`Text` or `Comment` column, which silently creates extra fields. PowerShell's
`Import-Csv` does **not** catch this (it drops the extra fields), so this uses
`Microsoft.VisualBasic.FileIO.TextFieldParser`, which reports the true
per-row field count.

**Wired into the build:** the `ValidateLocalizations` target in
`src/Keystone.Mod/Keystone.Mod.csproj` runs it `BeforeTargets="Build"`, so a
bad row fails `dotnet build` (with a `file:line` message) instead of reaching
the game. Runs regardless of `KeystoneDeploy`; incremental (re-runs only when
a loc CSV changes). Run standalone with
`powershell -File tools/validate-localizations.ps1`.

**Fix when it fires:** quote any field containing a comma, e.g.
`Key,"text, with comma","comment, with comma"`.

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

```bash
# Dead-tree centerpiece: a vanilla PineMatureDead trunk wrapped in the
# Keystone PineIvy mesh, plus scattered undergrowth + rocks around it
python tools/generate-flourish-blueprints.py \
    --prefix KeystoneDeadPine --dead-tree Pine \
    --plants Dandelion BlueberryBush \
    --decorations Pebble_Round_1 Pebble_Square_2 --decorations-per-blueprint 1-2 \
    --count 5 --seed 1
```

**`--dead-tree <Species>`** adds a *centerpiece*: the vanilla
`{Species}MatureDead` trunk at tile centre (identical across all
lifecycle phases — already dead) plus its fitted Keystone ivy
(`TreeIvy/{Species}Ivy`, vanilla pivot, overlapping the trunk). One tree
gets one ivy, always together. Known species: `Pine Birch Maple Oak
Chestnut Mangrove`. Composes with `--plants`/`--decorations`, which then
scatter *around* the trunk (kept clear of centre by
`CENTERPIECE_CLEARANCE`). A tree-only blueprint (no `--plants`) is valid.
`--ivy-variants` wires the ivy through `{Species}IvyDry` / `{Species}IvyDead`
on `#Dying` / `#Dead` — enable only once those meshes are authored.

```bash
# Overgrowth overlays: undergrowth that RINGS a host tree the blueprint
# doesn't itself contain (the dead/living tree is the host entity). 3-4
# wild plants per mini, kept 0.30 off-centre so they don't clip the trunk.
python tools/generate-flourish-blueprints.py \
    --prefix KeystoneOvergrowthMini \
    --plants Dandelion:2 BlueberryBush:2 Sunflower:2 \
    --plants-per-blueprint 3-4 --clear-center 0.30 --stage Mature \
    --count 10 --seed 33
```

**`--clear-center <radius>`** keeps all plants/decorations at least `radius`
tile units from tile centre, leaving a clear gap for a mesh the blueprint
does *not* contain itself — e.g. an overgrowth overlay draped on a
living/dead host tree, where the trunk is the host entity. Independent of
`--dead-tree` (which adds its own trunk + uses `CENTERPIECE_CLEARANCE`);
when both are given, `--clear-center` wins. Must be `< POSITION_RANGE`.

**Auto-registration.** Generated blueprints are appended (idempotently)
to the `KeystoneNaturalResources` TemplateCollection so the game loads
them — the collection is a hand-maintained explicit list, not
auto-discovery. Pass `--no-register` to skip, or `--collection <path>`
to target a different collection.

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
Once an idea is accepted, `promote-idea.ps1` moves it from Discussions to
Issues.

## `promote-idea.ps1`

Promotes a Discussion **idea** to a tracked **issue** once it lands on the
roadmap / enters development. There is no native `gh discussion` command, so the
discussion read/edit/close go through `gh api graphql`; issue creation uses
`gh issue create` and board placement uses `gh project`. Run from inside the
repo; requires `gh` authenticated (the board step additionally needs the
`project` token scope -- `gh auth refresh -s project`).

```powershell
.\tools\promote-idea.ps1 -DiscussionNumber 17
.\tools\promote-idea.ps1 -DiscussionNumber 17 -Label roadmap -Title "Sea and ocean biomes (MVP)"
.\tools\promote-idea.ps1 -DiscussionNumber 17 -NoBoard
```

In one shot it (1) creates an issue from the discussion's title/body with a
"Promoted from discussion #N" back-link, labelled `enhancement` by default;
(2) appends a forward-link ("Promoted to issue #M -- now under development")
to the discussion body via `updateDiscussion`; (3) closes the discussion
via `closeDiscussion` so the issue becomes the single source of truth for
active work; and (4) adds the new issue to the project board (default project
`#2`) with Status **Todo**. The board's Status field and target column are
resolved by name at run time, so adding/reordering columns won't break it. The
board step is secondary -- if it fails (e.g. missing `project` scope) the
issue + discussion changes still stand and a warning is printed.

`-Label <name>` sets the issue label (must already exist on the repo).
`-Title <text>` overrides the issue title (defaults to the discussion title).
`-CloseReason RESOLVED|OUTDATED|DUPLICATE` sets the close reason (default
`RESOLVED`). `-ProjectNumber <n>` targets a different board (default `2`).
`-BoardStatus <name>` sets the landing column (default `Todo`). `-NoBoard`
skips board placement entirely.

Note: this is the natural follow-up to `new-idea.ps1` -- ideas flow in via
Discussions and graduate to Issues here. Issues are no longer bug-only; the
label distinguishes bug reports (`bug`) from promoted ideas (`enhancement`).
