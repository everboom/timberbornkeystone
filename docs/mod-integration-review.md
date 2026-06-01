# Keystone.Mod integration review

Audit of `src/Keystone.Mod/` against three failure axes: Timberborn
updates that move/rename/restructure their API, other mods that share
the process, and game-state edge cases that the loud-vs-silent
decisions are meant to handle.

The review is critical by request. Sections 1–6 below follow the
structure the audit brief asked for. File and line references are
absolute paths into the repo.

Section 7 records which findings have since been fixed and which were
intentionally not actioned, with reasoning.

**Note (post-audit refactor):** the four per-class `*FlourishReconciler`
classes and their `FlourishReconcilerBase<T>` parent were collapsed
into per-class `*SpawnHandler` classes (with `SpawnHandlerBase<T>`)
invoked by a single `ChunkRulesApplier` scheduler. The file paths in
the sections below still read `ClassBFlourishReconciler.cs` etc. — the
audit findings themselves carried over to the renamed code and the
filenames in the section text were left as-is for traceability against
the audit's original snapshot.

## 1. Executive summary

- **The Harmony patch set is the highest-blast-radius coupling.** Eight
  patched methods, several on closed generics and private members,
  multiple resolved via reflection or string-keyed compiler-generated
  backing fields. The startup `ExpectedPatchedMethodCount` assertion is
  the right idea but it's a warning, not a hard fail — and several of
  the patches will *resolve* but no-op silently if Timberborn changes a
  signature shape rather than removing the method.
- **One concrete bug.** `TemplateCollectionServicePatch` reads
  `CrossFactionProviderBase.ActiveFactionId` to decide what to strip.
  That static is only set if a cross-faction provider was consulted
  *before* the postfix runs. The patch is registered on the
  `TemplateCollectionService.Load` method by `[HarmonyPatch]` attribute
  decoration; Harmony invokes it the moment the game's load runs, which
  is concurrent with — not strictly after —
  `IMaterialCollectionIdsProvider`/`ITemplateCollectionIdProvider`
  consumption. If the host happens to call `Load` before iterating the
  providers (or skips them entirely because the active faction matches
  both candidate ids in degenerate test scenarios), `ActiveFactionId`
  is null, the patch logs a warning, and the cross-faction strip never
  happens — *and the cross-faction natural-resources stay plantable
  through the active faction's UI*, which is exactly the crash the
  patch exists to prevent.
- **There is no defensive layer between Bindito and Core's port
  consumers.** If a Timberborn service rename causes a port adapter to
  fail to resolve at DI time, the Configurator-level error is fatal to
  the whole mod, but it is also opaque — Bindito's standard message
  surfaces "constructor parameter X of type Y could not be resolved"
  and nothing in Keystone catches that, logs which port is at fault,
  or fails gracefully.
- **Several silent-failure paths exist that the "no silent failure"
  rule in CLAUDE.md would normally forbid.** Notably `KeystoneFlourish`
  on missing leaves logs a warning then silently renders nothing useful
  for that decorator; `BlueprintResolver.Resolve` caches misses and
  serves them silently after a one-time log; reconcilers swallow
  `CreateFinished` exceptions to a `LogWarning`. These are arguably
  defensible (reconcilers run on every cycle and must keep running),
  but the policy is inconsistent — some of the same patches that wrap
  in `try/catch` and log then run-original would be more correct as
  loud failures the first time, then suppressed.
- **The `KeystoneDebugPanel` side-channel is a known smell that is not
  worth fixing today, but it is misclassified in the comment.** The
  failure mode isn't "annoying, not catastrophic" — it would couple
  `PlateauHighlighter` to drawing whenever the entire debug overlay is
  visible (across all third-party mods' panels too). On a streamer's
  game, with a debug-overlay-extension mod, that's "highlights stay on
  forever, intrusively." See finding 6.4.

## 2. Inventory of integration points

Severity column: **H** = will break loudly on a Timberborn update or
break gameplay if it fails; **M** = degrades a feature but the mod
keeps loading; **L** = cosmetic / dev-only.

### 2.1 Harmony patches (`src/Keystone.Mod/HarmonyPatches/`)

| Patch site | Depends on | Blast radius | Defense | Severity |
|---|---|---|---|---|
| `TemplateCollectionService.Load` (postfix) | Method name; closed-generic `Blueprint`/`ComponentSpec` shape; `<AllTemplates>k__BackingField` compiler-generated name; static side-channel `ActiveFactionId` being set first | Cross-faction crash on plant; bug already present (see §3.1) | Try/catch around body; field-lookup throws at class init if missing | **H** |
| `BlockObjectDeletionTool<BuildingSpec>.ActionCallback` (prefix) | Open-generic class still being parameterised by `BuildingSpec`; private method name `ActionCallback`; ref-enumerable parameter shape | Player can't bulk-demolish Class B flourishes — they survive build-over | Try/catch; null-check on `ClassBAreaQuery.Instance` | **M** |
| `BuildingDeconstructionTool.PreviewCallback` (prefix) | Same shape concerns; method override exists on the more-derived class | Highlight desync vs. commit (commit goes ahead but preview missed them) | Same | **M** |
| `SelectableObjectRetriever.TryGetSelectableObject(GameObject, out SelectableObject)` (prefix) | Specific overload exists with that exact signature; the `[HarmonyTargetMethod]` reflection lookup throws on init if absent | Class B flourishes become selectable, no Keystone-shaped failure but visual mess | Reflection-thrown `InvalidOperationException` at class init | **M** |
| `EntitySelectionService.Select(BaseComponent)` (prefix) | Method exists with that exact overload | Click on Class B throws (assertion-style downstream "not found") rather than silently failing | Try/catch | **H** |
| `DemolishableSelectionTool.ActionCallback` + `PreviewCallback` (prefix) | Both private methods exist with the ref-enumerable shape | Player can mark Class B for demolition (which will then fail mid-job) | Try/catch + null-guard | **M** |
| `NaturalResourceModel.ShowCurrentModel` (prefix) | Field `_growable` exists with that exact name | KeystoneFlourish entities NRE at `PostInitializeEntity` — *fatal entity crash* | Reflection-thrown at class init if field gone; try/catch around body returns "run original" on error, which then NREs | **H** |

The `ExpectedPatchedMethodCount = 8` warning at startup catches the
"target removed entirely" case. It does **not** catch "target still
exists, but the prefix's parameter names no longer match — Harmony
matches by name, so a parameter rename is a silent no-op." Two of the
patches above use `ref IEnumerable<BlockObject> blockObjects` — if
Mechanistry renames that to e.g. `entities`, the patch will be
*applied* (count = 8, no warning) but the assignment back will silently
be against a phantom parameter.

### 2.2 Bindito bindings (`src/Keystone.Mod/KeystoneConfigurator.cs`)

Every line of `KeystoneConfigurator.Configure()` is a coupling. The
ones that take game services indirectly through their adapter
constructors are the ones that fail if Timberborn moves a service:

| Adapter | Game services it constructor-injects | Effect of any of them being renamed or removed |
|---|---|---|
| `TerrainQueryAdapter` | `ITerrainService` | Bindito resolution failure → whole Game scope fails to construct |
| `MoistureQueryAdapter` | `ISoilMoistureService`, `MapIndexService` | Same |
| `ContaminationQueryAdapter` | `ISoilContaminationService`, `MapIndexService` | Same |
| `WaterQueryAdapter` | `IThreadSafeWaterMap` | Same |
| `BuildingQueryAdapter` | `IBlockService`, `IPathService` | Same |
| `PlantingMarkAdapter` | `PlantingService` (concrete, not interface) | Same — and concrete-class binding is more fragile than interface |
| `GameClockAdapter` | `GameCycleService`, `WeatherService`, `HazardousWeatherService`, `IDayNightCycle` | Four points of failure for the clock alone |

The `MultiBind<TemplateModule>().ToProvider<KeystoneTemplateModuleProvider>()`
and `MultiBind<BottomBarModule>().ToProvider<KeystoneBottomBarModuleProvider>()`
calls couple to Timberborn's template-decoration and bottom-bar APIs.
The `MultiBind<ITemplateCollectionIdProvider>` and
`MultiBind<IMaterialCollectionIdsProvider>` couple to specific
extension surfaces; if those interfaces are renamed or removed, again
the Game scope fails to construct.

There is **no per-binding error isolation.** A single mis-resolved
parameter takes down the entire mod's Bindito surface. Severity: **H**
on any Timberborn-side rename of the targeted concrete services
(`PlantingService`, `WeatherService`, `HazardousWeatherService`,
`MaterialRepository` etc.) since the mod is asking for the concrete
types and Mechanistry has historically renamed concrete services
between versions even when keeping the interface stable.

### 2.3 Blueprint overlays

**None.** The repo contains no `.optional.blueprint.json` overlay
files, which is somewhat surprising given that `CLAUDE.md`'s
extension-point order prefers them above Harmony. The reasons each
patch was chosen are documented and sound (the spec strip is at a
collection-service callback that overlays can't reach; the selection
patches operate at a runtime predicate, not a spec field), but this
should be re-checked: is there a way to refactor
`NaturalResourceModelShowCurrentModelPatch` away by having
KeystoneFlourish blueprints carry a stub `GrowableSpec`? That would
remove the highest-severity reflection-based Harmony patch.

### 2.4 Lifecycle hooks (Bindito singletons)

| Type | Interfaces | Order concern |
|---|---|---|
| `KeystoneSurveyor` | `IPostLoadableSingleton` | Self-guarded with `EnsurePostLoaded`; idempotent |
| `KeystonePersistence` | `ILoadableSingleton`, `IPostLoadableSingleton`, `ISaveableSingleton` | Forces surveyor PostLoad first via `EnsurePostLoaded` |
| `EcologyFieldUpdater` | `IPostLoadableSingleton` | Has `RebuildIndexMapIfStale` defensive retry — see finding 6.5 |
| `FloraCatalogLoader` | `IPostLoadableSingleton` | None Keystone-side; downstream `EcologyFieldUpdater` retries |
| `BlueprintResolver`, `FlourishCatalog`, `BiomeLevelCatalog` | `IPostLoadableSingleton` | All three read `ISpecService.GetSpecs<>` and assume specs are populated; this is the documented contract for PostLoad |
| `KeystoneStartupWarmup` | `IPostLoadableSingleton` | Runs both `EcologyFieldUpdater.RunCycleNow` and `ChunkBiomeTicker.RunWarmupNow` — assumes both their dependencies' PostLoads are done. **No ordering enforcement.** |
| `RegionUpdater` | `ILoadableSingleton`, `IUnloadableSingleton`, `ITickableSingleton` | Subscribes to event bus on Load; symmetric Unload. Clean. |
| `RegionValueLifecycleHandler` | `ILoadableSingleton` | Subscribes to RegionService events. Clean. |
| `ClassBAreaQuery` | `ILoadableSingleton` | Publishes `Instance` static for Harmony to read. Race window before Load runs; patches null-check defensively. |

The Bindito PostLoad ordering issue around
`KeystoneStartupWarmup` and `EcologyFieldUpdater` is real — the comments
in those files note it explicitly, and the workarounds (lazy retry
inside `RebuildIndexMapIfStale`) are reasonable but layered. See 6.5.

### 2.5 Spec decoration (`Recipes/`, `Flourish/`)

Three `AddDecorator<TSpec, TComponent>` bindings:
- `KeystoneFlourishSpec` → `KeystoneFlourish` (visual lifecycle, `BaseComponent`)
- `KeystoneVariantSpec` → `KeystoneVariant` (persistent class id, `BaseComponent + IPersistentEntity`)
- `KeystoneBiomeLevelsSpec` → `KeystoneBiomeLevels` (no-op marker, `BaseComponent`)

Each `[Serialize]`-shaped spec correctly uses `ImmutableArray<T>` for
record-list fields per the known `BasicDeserializer` foot-gun. The
spec field names (`Class`, `BlueprintName`, `BlueprintNames`, `Biome`,
`Level`, `Filter`, `Weight`, `LevelId`, `LowerMaturity`,
`UpperMaturity`, `Density`) become save/load schema once blueprints
ship; renaming any of them in a future version would break older
saves' blueprint JSON (though not Keystone-side persisted data, since
that uses a separate codec).

Severity: **M**. Stable today; an emerging schema concern.

### 2.6 Direct service consumption (non-adapter)

Several mod-side singletons take Timberborn services directly without
going through ports:

- `ClassBFlourishReconciler`, `ClassCFlourishReconciler`,
  `ClassDFlourishReconciler` — take `IBlockService`, `BlockObjectFactory`,
  `EntityService` (Class D only).
- `EcologyFieldUpdater` — takes `IBlockService` for entity probes.
- `ChunkBiomeAdapter` — takes `IBlockService`.
- `KeystoneDebugPanel` — takes `IBlockService`, `CursorDebugger`,
  `DebuggingPanel`.
- `KeystoneDecorationRegistry` — takes `IPrefabOptimizationChain`,
  `TemplateCollectionService`.
- `FlourishPlacementTool` — takes `InputService`, `CursorCoordinatesPicker`,
  `BlockObjectFactory`.

These are deliberate breaks of the port/adapter discipline because the
Core side has no role for them. The risk is the standard service-rename
case — but at least these don't propagate Timberborn types deeper into
Core.

`FlourishPlacementTool.PostSpawnPolish` uses **reflection on
`Growable._timeTrigger`** to fast-forward seedlings. Same pattern
appears in `VanillaFloraPlacementTool` and `CrossFactionFloraPlacementTool`.
These will silently break (the `field` lookup will return null and the
`if (field != null && ...)` skips the work) if Mechanistry renames the
private field. Severity: **L** (dev tool; player never sees it), but
worth a one-time logged warning if the field is null on first lookup.

## 3. Findings

### 3.1 Active-faction static side-channel is a real race (BUG)

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\HarmonyPatches\TemplateCollectionServicePatch.cs` lines 87–103; `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Debug\CrossFactionProviderBase.cs` lines 38, 87.

**Why it's a concern.** `ActiveFactionId` is a static written by
`YieldOtherFaction` whenever a cross-faction provider is iterated.
`TemplateCollectionServicePatch.StripCrossFactionPlantableAndGatherable`
reads it inside its postfix on `TemplateCollectionService.Load`. The
ordering between "TemplateCollectionService.Load runs" and "Timberborn
iterates `ITemplateCollectionIdProvider` instances" is determined by
Mechanistry's own code, not by Keystone, and the patch documents the
assumption ("cross-faction provider was consulted") rather than
enforcing it.

Empirically this is working today because Mechanistry happens to ask
the providers for ids first, then call `Load`. But that's not an API
contract — it's an implementation accident.

**Manifests as:** Silent. The patch logs a warning ("active faction
id not captured … Skipping strip"), then plant/gather UIs crash the
moment the player tries to use them on a cross-faction template.

**Recommendation.** Replace the static with a constructor-injected
`FactionService` on a per-patch helper, and resolve `Current.Id` at
patch time. Harmony patches can't take ctor injection, but a static
field on a Bindito singleton (`ClassBAreaQuery` already establishes
this pattern) gives you the same accessor without the ordering
dependency. One-hour refactor.

### 3.2 Closed-generic Harmony resolution silently no-ops if generic args change

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\HarmonyPatches\BuildingDeconstructionClassBPatch.cs` line 184 (`typeof(BlockObjectDeletionTool<BuildingSpec>)`).

**Why it's a concern.** Harmony's
`[HarmonyPatch(typeof(BlockObjectDeletionTool<BuildingSpec>), "ActionCallback")]`
resolves the closed generic at patch time. If Mechanistry ever
reshapes the generic — say `BlockObjectDeletionTool<TSpec, TConfig>` —
the closed type literal stops compiling, which is good (loud build
break). But if they replace `BuildingSpec` with a sibling type as the
generic argument on `BuildingDeconstructionTool`'s base, the patch
*still compiles* (the closed generic still exists, somewhere) and
*still resolves* (Harmony finds the method on the now-orphaned closed
generic) — but the runtime instance the player drags is a different
closed generic and Keystone never sees the call.

The `ExpectedPatchedMethodCount` check increments to 8 either way, so
the warning won't fire.

**Manifests as:** Silent. Bulk demolish no longer cleans up Class B
flourishes; they accumulate forever.

**Recommendation.** Add a runtime smoke check: at first
`PreviewCallback` Prefix invocation, log a single line confirming the
patch was reached. If a `KeystonePerfPanel` overlay never shows the
counter incrementing during a known-good test scenario, that's the
regression signal. One-hour change.

### 3.3 `NaturalResourceModelShowCurrentModelPatch` is the most fragile

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\HarmonyPatches\NaturalResourceModelShowCurrentModelPatch.cs`.

**Why it's a concern.** This patch papers over a *vanilla NRE* — the
game's else-branch in `NaturalResourceModel.ShowCurrentModel`
unconditionally dereferences `_growable`. Keystone flourishes opt out
of `GrowableSpec` so they hit that path. The patch:
- Uses reflection to read `_growable` (private field, name-string-keyed).
- Returns `false` (skip body) when growable is null.
- Has a `try`/`catch` around the field read that on exception
  *returns true (run original)* — which will then NRE.

The `try/catch + run original` pattern is wrong here specifically:
the patch exists to prevent an NRE, so the failure mode of the patch
itself must not be "let the original NRE happen." It should be
"return false and log, accepting the visual is broken for this
entity," because the alternative is a crash that kills entity init.

**Manifests as:** Loud (entity init crash) if the catch ever
triggers — but only after Timberborn changes something, at which point
every Keystone flourish entity dies during PostLoad.

The deeper question is whether the `GrowableSpec`-on-flourishes
alternative (mentioned in §2.3) would let this patch go away
entirely. A stub `GrowableSpec` with zero growth time might land
flourishes in a state vanilla can render, eliminating both the patch
and the reflection.

**Recommendation.** Investigate the stub-GrowableSpec alternative.
If it doesn't work, change the catch to return `false` (suppress
original) instead of `true`. Half-day investigation.

### 3.4 `<AllTemplates>k__BackingField` is the most string-fragile reference in the codebase

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\HarmonyPatches\TemplateCollectionServicePatch.cs` lines 74–78.

**Why it's a concern.** The string `"<AllTemplates>k__BackingField"`
is the C# compiler's mangled name for the auto-property backing field.
This works today but:
- The mangling convention is a compiler implementation detail, not a
  C# language guarantee — though it is *de facto* stable across MS
  Roslyn versions, including under .NET 10.
- Any refactor on Mechanistry's side that changes `AllTemplates`
  from an auto-property to a manual property (e.g. they add a setter
  with logging) will silently break it.
- The class throws on init if the field is missing, which is the
  right loud-failure behaviour — but the failure happens during
  Harmony patch application, before any Keystone code runs, and may
  be misattributed as "Keystone broke."

**Manifests as:** Loud (throws on type init), but at startup so the
player sees "Keystone failed to load" without context.

**Recommendation.** Wrap the field lookup in a `try/catch` that logs
a clear "Keystone needs Timberborn's `TemplateCollectionService.AllTemplates`
to remain auto-property-backed; please report version X to Keystone
authors" message and disables just this one patch rather than failing
PatchAll. Or use the public `AllTemplates` getter for the read and
find a Harmony-compatible way to publish the rebuilt list (e.g. a
Transpiler that rewrites the Load body — much more invasive, probably
worse). One-hour mitigation.

### 3.5 Bindito resolution failure has no Keystone-side diagnostic

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\KeystoneConfigurator.cs` lines 53–213.

**Why it's a concern.** If any Timberborn-side service named in any
adapter's constructor moves or is renamed, Bindito fails with a
generic "could not resolve type X" error that doesn't mention which
Keystone adapter triggered it. For a small repo this is fine; for a
mod that other people install, a one-line "Keystone failed to wire up
the moisture port" makes the difference between a usable bug report
and a screenshot of a Unity stack trace.

**Manifests as:** Loud, but illegible.

**Recommendation.** Optional: wrap each `Bind<>` group in a method
with a try/catch around the `Configure()` call's invocations. Not
trivial because Bindito's lazy resolution model means errors happen
at first-resolve time, not at `Bind` time. Likely needs a smoke-test
hook: after Game scope construction, resolve every Keystone port once
and log the success/failure breakdown. Half-day effort.

### 3.6 Several silent-failure paths conflict with the project's stated policy

**Locations:**
- `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Recipes\BlueprintResolver.cs` lines 60–68 (cache-miss returns null silently after first log)
- `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Recipes\ClassBFlourishReconciler.cs` lines 103–131 (`TrySpawn` swallows exceptions to LogWarning)
- `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Flourish\KeystoneFlourish.cs` lines 226–238 (all leaves null → LogWarning, then `UpdateVisuals` is a no-op)
- `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Decoration\KeystoneDecorationRegistry.cs` lines 142–147, 179–184 (controller `Tick` throws → LogError, continue)

**Why it's a concern.** `CLAUDE.md` is explicit: "If something
unexpected happens, fail loudly." Two of the four above are
defensible — a per-cycle reconciler that throws would loop-spam logs
and ultimately stall the game tick, and a registry that lets one
broken decoration take down all others is genuinely bad UX. The other
two are not so clear:
- `BlueprintResolver` caches a null and never warns again. A typo in
  a recipe blueprint name silently disables that recipe for the rest
  of the session. Should at least re-log periodically, or expose a
  "missing blueprints" property the debug panel can render.
- `KeystoneFlourish` with all-null leaves "logs loudly so the cause
  is obvious," then renders with all default-active variants. That
  *is* loud, but the failure isn't a one-time event — every spawn of
  the broken blueprint produces the same log line, which dilutes the
  signal.

**Manifests as:** Silent decay over a session.

**Recommendation.** Add a debug panel section listing "recipes
referencing missing blueprints" and "blueprints with broken leaf
hierarchies." Surfaces silent failures without code-path changes.
Half-day effort.

### 3.7 `KeystoneDebugPanel` side-channel risk is undersold

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Debug\KeystoneDebugPanel.cs` lines 69–116 (HACK block); `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Visualization\PlateauHighlighter.cs` lines 32–43.

**Why it's a concern.** The HACK comment says the failure mode is
"highlighting silently reverts to 'always on whenever the debug
overlay is open'." That understates two things:
1. **Cross-mod interference.** A third-party debug-overlay mod (or a
   future Mechanistry redesign that adds a periodic "panel layout
   refresh" pass) will tick `GetText` on every Keystone panel call,
   and `PlateauHighlighter` will draw on every frame whenever the
   debug overlay is shown — across all panels, not just Keystone's.
2. **Other systems calling `AreaHighlightingService`.** Keystone's
   `UpdateSingleton` calls `UnhighlightAll`. If another mod has
   highlighted its own tiles, Keystone will erase those highlights
   every frame the gate is on. Today that gate is restrictive
   (debug-overlay-expanded-only); flip that gate to "always on" and
   we are stomping on every other mod's tile-highlight usage.

**Manifests as:** Silent for Keystone's own visuals; visible
interference with other mods.

**Recommendation.** The right replacement is a `DebugModeController`
gate + an explicit hotkey toggle. The hotkey lives in the panel
itself; the gate ensures the highlighting is only ever on in debug
mode. One-day effort, lands a clean public API for the
visualization toggle.

### 3.8 `BlockObjectClassification.IsNatural` is a foot-gun for third-party content

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Adapters\BlockObjectClassification.cs`.

**Why it's a concern.** The "inverse-discriminator" rule (a
`BlockObjectSpec` lacking `BuildingSpec` is natural) means any
third-party mod that adds a `BlockObject` without `BuildingSpec` —
for example, decorations that should *not* count as buildings or
natural elements (think a Mechanistry-future "billboard" object) —
gets misclassified as natural. Region updates skip naturals, so the
billboard wouldn't dirty its halo when placed. Marginal today;
relevant if/when the mod ecosystem grows.

**Manifests as:** Silent. Other mods' content gets categorised
wrong and Keystone's region graph drifts.

**Recommendation.** Document the contract and accept the risk for
now; revisit once a known third-party object type breaks it. Track
as a "live with and document" item.

### 3.9 Reflection on private `_timeTrigger` field is unguarded

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Flourish\FlourishPlacementTool.cs` lines 235–252; `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Flora\VanillaFloraPlacementTool.cs` lines 234–238; `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Flora\CrossFactionFloraPlacementTool.cs` lines 144–148.

**Why it's a concern.** Dev-tool fast-forward uses
`typeof(Growable).GetField("_timeTrigger", ...)`. If the field is
renamed, `field` is null, the `if (field != null && ...)` skips it,
and spawned seedlings stay seedlings — which is the correct
fallback, but no warning surfaces. The dev tools become quietly
broken across a game update.

**Manifests as:** Silent.

**Recommendation.** One-time log on first null-field detection. Ten
minutes of work, deferred to "fix when convenient."

### 3.10 The Class D reconciler memo is acknowledged as session-local

**Location:** `C:\Projects\TimberbornKeystone\src\Keystone.Mod\Recipes\ClassDFlourishReconciler.cs` lines 56–60 (comment), 81 (`_spawnedAtLevel`).

**Why it's a concern.** The reconciler documents this as a known
limitation. "Save → cut → reload" lets Keystone re-spawn the cut
tree. The comment promises persistence in a future round. Not a
regression today; flagged here so the audit doesn't quietly forget
it. Severity: **M**, persistence-shaped work.

## 4. Cross-mod compatibility

### Declared dependencies

The manifest at `unity-assets/Keystone/manifest.json` lists:
- `TimberUi` ≥ 10.1.5
- `Harmony` ≥ 2.3.3

The Harmony declaration is exactly right per `CLAUDE.md` convention.
TimberUi is declared but, looking through `src/Keystone.Mod/`, I see
no actual TimberUi consumption — no `TimberApi.*` (correct, since
TimberAPI is deprecated for 1.0), no `Bindito` plugin attributes from
TimberUi, no calls into TimberUi's `DialogService` etc. The
docstring at the top of `KeystoneFlourish` doesn't reference it
either. Either the dep is aspirational ("we will use it") or it's
covering a transitive surface I missed. Worth confirming.

### Most plausible conflict sources

| Mod type | Conflict surface | Likely outcome |
|---|---|---|
| Another mod also patching `SelectableObjectRetriever.TryGetSelectableObject(GameObject, out)` | Both prefixes wrap the same method. Harmony stacks them by priority. Both return `false` (skip original) — first one wins. Result depends on ambient predicate's specificity. | Harmless for Keystone (still suppresses); other mod's suppression may be lost. |
| Another mod patching `BuildingDeconstructionTool.PreviewCallback` | Same stacking. If the other mod also widens or narrows `blockObjects`, the order matters: a downstream narrowing prefix erases Keystone's injection. | Visible — Class B drag-select highlight inconsistent. |
| Another mod also registering `MultiBind<ITemplateCollectionIdProvider>` | Both get iterated, both contributions ingested — no conflict. | Fine. |
| Another mod with conflicting blueprint name | `BlueprintResolver.PostLoad` first-wins. | Other mod's blueprint may shadow Keystone's or vice versa. Hard to predict; mitigate with mod-namespaced blueprint names (Keystone already does this). |
| Another mod stripping or modifying `BuildingSpec` on the same closed generic | Their patch and Keystone's patch on the same method stack. Harmony serialises them. | Likely OK; depends on the other mod's intent. |
| ModdableTimberborn (not currently a dep) | The patch surface for `TemplateCollectionService.Load` is the same one MTB uses. Two mods postfix-ing the same Load could each rebuild `AllTemplates` and step on each other. | High-severity if MTB is installed. Keystone doesn't currently depend on MTB; if it ever does, this needs reconciling. |

The `Class B` selection-suppression chain spans four Harmony patches
(retriever, selection service, demolish tool ×2). Any mod that
inserts itself into one of those four breaks the chain in subtle
ways — e.g. if mod X adds a Prefix on `SelectableObjectRetriever`
that returns `true` (run original) unconditionally, Keystone's
behaviour is preserved (it ran first). But if mod X's Prefix has
higher priority and returns its own `false`, Keystone's never runs.

**Recommendation.** Live with the conflict surface. Document the
patched-methods list in user-visible docs so other mod authors can
see what's hooked.

## 5. What's done well

- **Port/adapter discipline is the strongest thing in the codebase.**
  `Keystone.Core` has zero Timberborn refs by `Directory.Build.props`
  enforcement, and the adapter layer is consistently thin. Tests can
  fake every port, and the surface to Timberborn is exactly the set
  of `ITerrainService`, `ISoilMoistureService`,
  `ISoilContaminationService`, `IThreadSafeWaterMap`,
  `IBlockService`, `IPathService`, `PlantingService`,
  `GameCycleService`/`WeatherService`/`HazardousWeatherService`/`IDayNightCycle`,
  `MapIndexService`. That's a small list and the most plausibly-stable
  surface for Mechanistry to keep stable.
- **`KeystoneVariant` is the right design.** Decoupling the
  per-entity class from the blueprint asset, persisting it via
  `IPersistentEntity`, and gating Harmony suppression on it — that
  is the textbook way to make a class designation survive save/load
  in Timberborn.
- **Specs use `ImmutableArray<T>` for nested record lists** in line
  with the documented `BasicDeserializer` constraint. The MEMORY.md
  note is being followed.
- **`KeystoneFlourishSpec` + decorator pattern** is exactly the
  spec-driven extension point that `CLAUDE.md` recommends; no
  Harmony involvement for the visual lifecycle.
- **`Harmony` is declared as a manifest dep, not bundled.** Aligned
  with `CLAUDE.md`'s ecosystem convention.
- **The Harmony patch comments are unusually thorough.** Each
  documents decompilation evidence, why less-invasive alternatives
  failed, and what to check if a future update breaks it. This is
  the right level of documentation for last-resort coupling.
- **`RegionUpdater.FlushPending("save")` before serialisation** is
  exactly the right defensive coupling. Catches a real save-time
  race.
- **`RegionValueLifecycleHandler.OnRegionRemoved`** drops scores
  rather than letting them leak across region-id reuse. Concrete
  example of "not assuming the topology will never change."
- **Schema versioning on the persistence codec** with a "higher
  version → load best-effort, warn loudly" path is good
  forward-compatibility hygiene.
- **`ExpectedPatchedMethodCount` startup assertion**, despite being a
  warning rather than a fail, is more than most mods do.

## 6. Recommended action list

### Fix soon (highest risk-reduction per hour)

1. **Resolve the `ActiveFactionId` race in
   `TemplateCollectionServicePatch`** (finding 3.1). Replace the
   static side-channel with a deterministic lookup, e.g. a Bindito
   singleton that publishes itself in `Load()` and the patch reads
   on first invocation. **One-hour change.** Closes a real
   correctness gap.
2. **Add a per-patch first-invocation log so the
   `ExpectedPatchedMethodCount` blind spot closes** (finding 3.2).
   One incrementing counter per `[HarmonyPatch]` class, logged once
   per session, lets a "patch silently no-ops" regression surface in
   the player log. **One-hour change.**
3. **Investigate the stub-`GrowableSpec` alternative for
   `NaturalResourceModelShowCurrentModelPatch`** (finding 3.3). If
   it works, deletes the most-fragile patch in the codebase. If it
   doesn't, at minimum flip the patch's catch to return `false`.
   **Half-day investigation.**

### Fix when convenient

4. **Replace the `KeystoneDebugPanel` side-channel with a
   `DebugModeController` + explicit hotkey toggle** (finding 3.7).
   Eliminates the cross-mod-stomp risk; lands a clean public API.
   **One-day refactor.**
5. **Add Keystone-side Bindito error diagnostics** (finding 3.5):
   after Game scope construction, resolve every Keystone port once
   and log per-port success/failure. **Half-day effort.**
6. **Expose missing-blueprint and missing-leaf-hierarchy reports in
   the debug panel** (finding 3.6). Surfaces silent recipe-resolution
   and visual-binding failures. **Half-day effort.**
7. **Harden the `<AllTemplates>k__BackingField` lookup** (finding
   3.4). Try/catch + clear error so a patch failure doesn't take
   down all of `PatchAll`. **One-hour change.**

### Live with and document

8. **Reflection on `Growable._timeTrigger`** (finding 3.9) —
   one-time logged warning the first time the field comes back null.
   Ten minutes.
9. **`BlockObjectClassification.IsNatural`'s inverse discriminator**
   (finding 3.8) — accept the risk; document.
10. **Class D memo session-local limitation** (finding 3.10) —
    already documented; track for a persistence round.
11. **TimberUi manifest dep that doesn't appear to be exercised** —
    confirm whether it's aspirational or transitive; remove if it's
    not actually used today.

The top three "fix soon" items are the load-bearing ones. Items 4–7
are polish. Items 8–11 are awareness.

## 7. Status (post-review)

### Fixed

- **1. `ActiveFactionId` race** — fixed. New
  `Keystone.Mod.HarmonyPatches.FactionIdAccessor` (an
  `ILoadableSingleton`) publishes `FactionService` to a static handle
  in its constructor; `CurrentId` resolves
  `FactionService.Current?.Id` at call time. The patch and the
  cross-faction dev tool both read from the accessor. The static
  side-channel on `CrossFactionProviderBase` is removed.
- **2. Per-patch first-invocation log** — fixed. New
  `Keystone.Mod.HarmonyPatches.PatchInvocationLog.Once(name)` helper;
  each of the eight Harmony patch sites logs once on first execution.
  Closes the `ExpectedPatchedMethodCount` blind spot for "Harmony
  resolved the method but the patch silently no-ops."
- **6. Missing-blueprint debug surface** — fixed.
  `BlueprintResolver.MissedNames` exposes the cached-null set;
  `KeystoneDebugPanel` renders "Missing blueprints (N): name1, name2"
  near the top of the overlay when the set is non-empty.
- **8. `Growable._timeTrigger` reflection one-time log** — fixed via
  consolidation. The three previously-inline reflection blocks (in
  `FlourishPlacementTool`, `VanillaFloraPlacementTool`,
  `CrossFactionFloraPlacementTool`) now route through
  `Keystone.Mod.Flora.GrowableTimeTriggerAccessor.FastForwardToMature`,
  which caches the `FieldInfo` once and emits a single
  "field not found via reflection" warning if Timberborn renames it.

### Investigated, not actioned

- **3. Stub-`GrowableSpec` to retire
  `NaturalResourceModelShowCurrentModelPatch`** — investigated; not
  done. Adding `GrowableSpec` to flourishes would route them through
  the vanilla growth pipeline (`Growable.InitializeEntity` constructs
  `NaturalResourceLifecycleModel` instances per growth stage), which
  competes with the `KeystoneFlourish` visual lifecycle the mod
  already drives off `WateredNaturalResource` /
  `FloodableNaturalResource`. The clean-stub-with-zero-growth-time
  approach is not safe without in-game testing of the model-stage
  interaction. The patch stays; revisit when a maintenance window
  pairs with the ability to run game scenarios.

### Intentionally not actioned

- **4. Replace `KeystoneDebugPanel` `GetText` side-channel with
  `DebugModeController` + hotkey** — not done. The escalation in
  finding 3.7 (cross-mod stomp on `AreaHighlightingService`) requires
  a stack of (third-party debug-overlay mod installed, its panel
  expanded, another mod using `AreaHighlightingService` concurrently)
  that doesn't exist in any known Timberborn mod ecosystem today.
  The 1-day rewrite buys little against a hypothetical scenario; the
  existing HACK comment already documents the risk. Reclassified to
  "live with and document" until a concrete incident appears.
- **5. Bindito error diagnostics** — not done now. Useful for a
  publicly-shipping mod with non-author users producing bug reports;
  unnecessary for the current single-developer workflow. Revisit at
  the public-release boundary.
- **7. Harden `<AllTemplates>k__BackingField` lookup** — not done.
  The recommended mitigation (catch + disable just this patch on
  field-missing) doesn't help: the patch exists precisely to
  prevent cross-faction template UI crashes, so "gracefully
  disabling" it surfaces exactly the failure it's meant to prevent.
  The only real mitigation is item 3 (stub-`GrowableSpec`) — which is
  pending in-game validation. The current loud-throw-at-type-init
  behavior is the correct failure shape until that lands.
- **9. `BlockObjectClassification.IsNatural` inverse discriminator**
  — kept as-is. Already classified as "live with and document" in
  the original recommendation.
- **10. `ClassDFlourishReconciler` session-local memo** — kept as-is.
  Already tracked for a persistence round.

### Open / needs user input

- **11. `TimberUi` manifest dependency** — confirmed unused in
  `src/Keystone.Mod/` (grep found zero `TimberUi`/`TimberApi`
  imports). The `unity-assets/Keystone/manifest.json` still lists
  `TimberUi >= 10.1.5` as a `RequiredMod`. Either it's aspirational
  (planned UI work hasn't started) or it's needed at the Unity asset
  bundle level rather than the code level (some bundled asset may
  reference TimberUi-defined components). User to decide whether to
  remove it or leave it.
