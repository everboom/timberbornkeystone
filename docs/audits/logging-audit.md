# Logging-strategy audit — Keystone (2026-05-22)

## TL;DR

A logging facade with a dev-gated verbose channel already exists (`KeystoneLog` + `KeystoneDevMode`, env var `KEYSTONE_DEV_MODE`). The strategy is realised for every call that goes through `KeystoneLog.Verbose`. The headline gap is **partial adoption**: about half of the ~95 raw `UnityEngine.Debug` call sites still bypass the facade. Many are legitimate `LogError`/`LogWarning` (real player-visible problems); a meaningful subset — chiefly the dev-only placement tools — emit `LogWarning` for routine "invalid click" outcomes that should be `Verbose`. **No truly silent `catch { }` blocks exist.** The project's "always Release" toggle means `#if DEBUG` would be dead, and indeed none is used; the env-var gate is the right design.

---

## 1. Logging surfaces

### 1.1 The facade — `src\Keystone.Mod\Diagnostics\KeystoneLog.cs`

| Method | Routed to | Gated? |
|--------|-----------|--------|
| `Info`    | `UDebug.Log`        | Always-on (only call: "Loaded successfully") |
| `Verbose` | `UDebug.Log`        | `if (!IsVerbose) return;` |
| `Warn`    | `UDebug.LogWarning` | Always-on |
| `Error`   | `UDebug.LogError`   | Always-on |

`KeystoneLog.IsVerbose` is initialised in `KeystoneModStarter.StartMod` (`src\Keystone.Mod\KeystoneModStarter.cs:82`) from `KeystoneDevMode.IsEnabled`, which probes `KEYSTONE_DEV_MODE` (Process target, then User registry).

### 1.2 Core (`src\Keystone.Core\`)

Core has **no logging** of its own (correct, by the netstandard-2.1 isolation rule). Two parsers (`BiomeLevelEntryValidator`, `AttritionRecipeParser`) take an `Action<string>` warning callback, which Mod-side adapters bind to `UDebug.LogWarning`; tests bind it to a list.

### 1.3 Inventory (raw `UDebug.*` sites only — `KeystoneLog.Verbose` calls are correctly gated, ~80 of them)

| Subsystem | Files (raw `UDebug.*` sites) |
|-----------|------------------------------|
| **Persistence** | `Persistence\KeystonePersistence.cs` (5: schema-version warning at 189; dropped-stamp warnings at 297, 305; z-mismatch error at 413; save-drop warning at 520) |
| **Assets / bundle** | `Assets\KeystoneAssetService.cs` (2 errors at 89, 99) |
| **Atmosphere** | `Atmosphere\WetlandMistDirector.cs` (errors at 536, 605) |
| **Biomes** | `Biomes\ChunkClusterTicker.cs` (errors at 134, 145) |
| **Flora / placement tools** | `Flora\VanillaFloraPlacementTool.cs` (5: 143, 160, 166, 174, 182); `Flora\CrossFactionFloraPlacementTool.cs` (4: 135, 145, 154, 202); `Flora\GrowableTimeTriggerAccessor.cs` (1: 85); `Flourish\FlourishPlacementTool.cs` (5: 139, 156, 162, 170, 190); `Flourish\KeystoneFlourish.cs` (3: 317, 332, 341); `Flourish\KeystoneRockTint.cs` (1: 168) |
| **Fauna** | `Fauna\FaunaSpawnDrainer.cs` (3: 421, 429, 441); `Fauna\FaunaPlacementTool.cs` (6: 132, 149, 156, 176, 185, 199); `Fauna\FishSmokeTestTool.cs` (5: 127, 145, 173, 182, 192); `Fauna\KeystoneFaunaAnimator.cs` (2: 77, 94) |
| **Decoration** | `Decoration\KeystoneDecorationRegistry.cs` (3 errors: 154, 196, 235); `Decoration\RockClusterPlacementTool.cs` (2: 136, 159); `Decoration\RockPlacementTool.cs` (4: 154, 255, 262, 269); `Decoration\DecorationPlacementTool.cs` (1: 90) |
| **Recipes** | `Recipes\AttritionHandler.cs` (1: 327); `Recipes\FlourishCatalog.cs` (~10); `Recipes\RecipeFilterRegistry.cs` (1: 49); `Recipes\BlueprintResolver.cs` (1: 91); `Recipes\BiomeLevelCatalog.cs` (1: 85) |
| **Toolbar / startup** | `Toolbar\KeystoneToolGroup.cs` (1: 104); `KeystoneModStarter.cs` (2: 89, 116) |
| **HarmonyPatches** | 7 patch files with try/catch→`LogError` in `Prefix`/`Postfix` (EntitySelectionServicePatch, DemolishableSelectionToolPatch x2, BuildingDeconstructionClassBPatch x2, NaturalResourceModelShowCurrentModelPatch, TemplateCollectionServicePatch, SelectableObjectRetrieverPatch; + one `LogWarning` at TemplateCollectionServicePatch:541) |
| **Debug probes** | `Debug\KeystoneSpawnProbe.cs`, `StrippedEntityProbe.cs`, `DerivedBlueprintProbe.cs`, `PassiveObjectProbe.cs`, `ParticleProbe.cs`, `MeshPathProbe.cs`, `BlockingCandidateProbe.cs`, `CrossFactionProviderBase.cs` (~25 sites total, all in dev-only probes) |
| **Wellbeing** | `Wellbeing\KeystoneNatureSource.cs` (3 errors: 184, 189, 200) |

---

## 2. Dev-vs-release gating

| Mechanism | Present? | Notes |
|-----------|----------|-------|
| `#if DEBUG`           | No (would be dead — Release-forced via `Directory.Build.props`) |
| Runtime flag          | **Yes**: `KeystoneLog.IsVerbose` driven by `KeystoneDevMode.IsEnabled` |
| Per-call log level    | Yes via the facade's 4 methods |
| Env-var probe         | `KEYSTONE_DEV_MODE` — registry-direct read, survives Steam env-snapshot |

The mechanism is sound. The gap is in call sites that **bypass the facade**.

---

## 3. Per-call classification

### 3.1 Correctly routed `KeystoneLog.Verbose` (no action)
~80 sites; tickers, persistence post-load summary, catalog dumps, placement-tool success traces, fauna cycle ticks, decoration registration. These already vanish in release.

### 3.2 Legitimate `UDebug.LogError` — keep loud, but migrate to `KeystoneLog.Error` for consistency

These describe states that genuinely leave the mod broken or unrecoverable, with catch-and-log correct because the work runs in a tick/event loop that must keep running:

- `Assets\KeystoneAssetService.cs:89, 99` — bundle / asset missing.
- `Atmosphere\WetlandMistDirector.cs:536, 605` — null prefab and spawn-exception inside per-tile loop.
- `Biomes\ChunkClusterTicker.cs:134, 145` — per-region rebuild exception inside rolling sweep.
- All seven `HarmonyPatches\*.cs` LogError sites — Harmony postfixes must not throw back into the patched method.
- `KeystoneModStarter.cs:89` — `PatchAll` failure; mod is broken if this fires.
- `Decoration\KeystoneDecorationRegistry.cs:154, 196, 235` — controller Tick exception.
- `Recipes\AttritionHandler.cs:327` — per-entity loop, comment explicitly justifies the catch.
- `Wellbeing\KeystoneNatureSource.cs:184, 189, 200` — entity-init failure (missing spec / Enterable / unknown biome).
- `Flourish\KeystoneRockTint.cs:168` — verify-once tint failure.
- `Flourish\KeystoneFlourish.cs:332, 341` — event-handler catch.

### 3.3 Legitimate `UDebug.LogWarning` — keep, migrate to `KeystoneLog.Warn`

Player- or mod-author-facing warnings for routine-but-noteworthy degradations:

- `Recipes\FlourishCatalog.cs` (~10 sites): missing Category, unknown Class, empty BlueprintName, unknown BiomeKind, empty BlueprintNames.
- `Recipes\RecipeFilterRegistry.cs:49` — unknown filter, with `_warnedNames.Add` dedupe (good pattern).
- `Recipes\BlueprintResolver.cs:91` — missing blueprint.
- `Persistence\KeystonePersistence.cs:189, 297, 305, 520` — save schema/dropped records.
- `Fauna\FaunaSpawnDrainer.cs:421, 429, 441` — recipe references unshipped blueprint / orphan agent.
- `Toolbar\KeystoneToolGroup.cs:104` — missing `ToolGroupSpec` (already inside dev-gated branch).
- `HarmonyPatches\TemplateCollectionServicePatch.cs:541` — divergent duplicate blueprint (deliberately loud — docstring spells it out).
- `Flora\GrowableTimeTriggerAccessor.cs:85` — vanilla schema change, guarded by `_warnedMissing` (good pattern).
- `KeystoneModStarter.cs:116` — Harmony patch-count drift.
- `Flourish\KeystoneFlourish.cs:317` — entire decorator hierarchy missing (real load-bearing warning).
- `Recipes\BiomeLevelCatalog.cs:85` — biome/level-entry validator surfacing.

### 3.4 Miscategorised — dev-noise, should be `KeystoneLog.Verbose`

These warnings fire on **routine player actions** (clicking an ineligible tile with a dev tool) or describe **expected** diagnostic states. They will spam any log with `KEYSTONE_DEV_MODE` set, and clutter dev logs unnecessarily:

- `Flourish\FlourishPlacementTool.cs:139, 156, 162, 170, 190` — "no region at tile", "no ecology field", "no recipe applies", "spawn threw", "CreateFinished returned null (tile occupied?)". Every one is a normal "user clicked somewhere invalid" outcome of a dev tool. *Demote to Verbose.*
- `Flora\VanillaFloraPlacementTool.cs:143, 160, 166, 174, 182` — same pattern.
- `Flora\CrossFactionFloraPlacementTool.cs:135, 145, 154, 202` — same pattern.
- `Fauna\FaunaPlacementTool.cs:132, 149, 156, 176, 185, 199` — same pattern.
- `Fauna\FishSmokeTestTool.cs:127, 145, 173, 182, 192` — same pattern.
- `Decoration\RockPlacementTool.cs:154, 255, 262, 269`, `RockClusterPlacementTool.cs:136, 159`, `DecorationPlacementTool.cs:90` — same pattern.
- All ~25 raw warning/error sites under `Debug\*.cs` probes. The probes are dev-only diagnostic tools; their "threw" lines describe expected diagnostic outcomes, not real problems.

Note: the placement tools are themselves gated behind `KeystoneDevMode` in `KeystoneToolGroup.GetElements()` (line 100), so in a clean release these can't fire — but cleanup still matters for the dev workflow where real warnings get lost in placement-tool chatter.

### 3.5 Marginal

- `Fauna\KeystoneFaunaAnimator.cs:77, 94` — PlayClip exception. Fires potentially per-frame. Argue for `Verbose` (the animator handles nulls cleanly downstream); if kept as warning, add a per-agent dedupe.
- `Recipes\ClassBSpawnHandler.cs:147` uses `KeystoneLog.Warn` where parallel C/D handlers use `KeystoneLog.Verbose` for the same logical condition (placement rejected). One of the two is wrong — pick.

---

## 4. Silent-failure scan

Independently grepped for empty catches and unlogged early returns across all 33 files containing `catch`:

- **Zero truly empty catches** (`catch { }`).
- **One bare `catch { continue; }`** in `Debug\PassiveObjectProbe.cs:179` — dev-only probe enumerating templates; `_prefabChain.Process(bp)` throws are expected for broken prefab chains. Defensible inside a dev tool; would violate the rule outside one.
- **One `catch (Exception) { return null; }`** in `Diagnostics\KeystoneDevMode.cs:75` — swallows env-var reads from sandboxed/non-Windows hosts. Anticipated, documented in the file's own comment; matches the global rule.
- Every other `catch` block logs (verified across all 33 files).

The `Class[BCD]SpawnHandler.cs` files catch placement exceptions and log `KeystoneLog.Verbose` with docstrings stating "expected — tile not usable, not a real fault." Correct (anticipated condition, logged at Verbose).

**No silent-failure violations of the project's rule.**

---

## 5. Recommendations (prioritised)

### Priority 1 — close the strategy gap
Convert dev-noise raw warnings to `KeystoneLog.Verbose` (concrete file:line list in §3.4 above). Single change with most observable effect: dev with KEYSTONE_DEV_MODE on still gets the trace; player without it stops seeing it.

### Priority 2 — facade consistency
Migrate the kept `UDebug.LogError` / `UDebug.LogWarning` sites onto the facade so the future mod-settings UI has one toggle surface for all four severities. Mechanical replacement; no semantic change. Especially the lists in §3.2 and §3.3.

### Priority 3 — resolve mixed-severity site
`Recipes\ClassBSpawnHandler.cs:147` vs ClassC/D parallel paths — pick one severity for "placement rejected".

### Priority 4 — pattern reinforcement
The `_warnedNames` / `_warnedMissing` / `_warnedUnknownBiomes` one-shot-warn patterns in `RecipeFilterRegistry`, `GrowableTimeTriggerAccessor`, and `KeystoneNatureSource` are good. Adopt for any new always-on warning that fires inside a per-entity / per-frame loop. (`FaunaSpawnDrainer:421` could use this — fires per spawn attempt against the same missing blueprint.)

### What's *not* missing
- No silent-failure violations.
- No `Console.WriteLine` anywhere in `src\`.
- No `Debug.LogException` (which auto-dumps the caller's stack frame instead of the exception's); the manual `{ex.GetType().Name}: {ex.Message}` pattern is consistent.
- No throws were found at API boundaries where the global rule would expect one — most "loud failures" are constructor-time DI surfaces where Bindito NREs naturally if a binding is wrong, which is adequate for now.
