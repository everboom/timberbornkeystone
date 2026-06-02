using Bindito.Core;
using Keystone.Core.Biomes;
using Keystone.Core.Buildings;
using Keystone.Core.Ecology.Fields;
using Keystone.Core.Flora;
using Keystone.Core.Persistence;
using Keystone.Core.Ports;
using Keystone.Core.Regions;
using Keystone.Core.Spatial;
using Keystone.Core.Survey;
using Keystone.Core.Time;
using Keystone.Mod.Adapters;
using Keystone.Mod.Assets;
using Keystone.Mod.Biomes;
using Keystone.Mod.Buildings;
using Keystone.Mod.Debug;
using Keystone.Mod.Decoration;
using Keystone.Mod.Diagnostics;
using Keystone.Mod.Diagnostics.SelfTests;
using Keystone.Mod.Diagnostics.StartupChecks;
using Keystone.Mod.Ecology;
using Keystone.Mod.HarmonyPatches;
using Keystone.Mod.Flora;
using Keystone.Mod.Materials;
using Keystone.Mod.Planting;
using Keystone.Mod.Flourish;
using Keystone.Mod.Growth;
using Keystone.Mod.Persistence;
using Keystone.Mod.Recipes;
using Keystone.Mod.Startup;
using Keystone.Mod.Survey;
using Keystone.Mod.Toolbar;
using Keystone.Mod.Visualization;
using Timberborn.BlueprintSystem;
using Timberborn.EntityPanelSystem;
using Timberborn.Growing;
using Timberborn.BottomBarSystem;
using Timberborn.TemplateCollectionSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.TimbermeshMaterials;

namespace Keystone.Mod {

  /// <summary>
  /// Bindito root configurator for the Keystone mod. Registers Keystone
  /// services into Timberborn's "Game" DI scope, which is active while a
  /// settlement is loaded.
  ///
  /// Bindings come in three groups:
  /// <list type="bullet">
  ///   <item>Port → adapter bindings, so Core consumers can resolve their
  ///         Keystone-defined ports without ever naming a Timberborn type.</item>
  ///   <item>Core services that depend on those ports.</item>
  ///   <item>Mod-side singletons that bridge Core into Timberborn lifecycle
  ///         and UI hooks.</item>
  /// </list>
  /// </summary>
  [Context("Game")]
  public class KeystoneConfigurator : Configurator {

    /// <inheritdoc />
    protected override void Configure() {
      // Ports → adapters.
      Bind<ITerrainQuery>().To<TerrainQueryAdapter>().AsSingleton();
      Bind<IMoistureQuery>().To<MoistureQueryAdapter>().AsSingleton();
      Bind<IContaminationQuery>().To<ContaminationQueryAdapter>().AsSingleton();
      Bind<IWaterQuery>().To<WaterQueryAdapter>().AsSingleton();
      Bind<IBuildingQuery>().To<BuildingQueryAdapter>().AsSingleton();
      Bind<IBlockingQuery>().To<BlockingQueryAdapter>().AsSingleton();
      // Concrete-then-ToExisting so Bindito honours
      // ILoadableSingleton / IUnloadableSingleton on the adapter
      // (the bucket needs to be populated at Load and event
      // subscriptions need to be released at Unload).
      Bind<PlantingMarkAdapter>().AsSingleton();
      Bind<IPlantingMarkQuery>().ToExisting<PlantingMarkAdapter>();
      Bind<ICuttingMarkQuery>().To<CuttingMarkAdapter>().AsSingleton();
      Bind<INaturalResourceAtTileQuery>().To<NaturalResourceAtTileAdapter>().AsSingleton();
      Bind<INaturalResourceEnumerator>().To<NaturalResourceEnumeratorAdapter>().AsSingleton();
      Bind<IClock>().To<GameClockAdapter>().AsSingleton();

      // Core services.
      Bind<TerrainSurveyor>().AsSingleton();
      Bind<PlateauFinder>().AsSingleton();
      Bind<RegionService>().AsSingleton();
      Bind<FloraCatalog>().AsSingleton();
      Bind<BuildingCatalog>().AsSingleton();
      Bind<RegionValueStore>().AsSingleton();
      Bind<ChunkValueStore>().AsSingleton();
      Bind<ChunkValueRegistry>().AsSingleton();
      Bind<ChunkDataStore>().AsSingleton();
      Bind<ChunkDataStoreReader>().AsSingleton();
      Bind<IChunkBiomeValues>().ToExisting<ChunkDataStoreReader>();
      // Spatial reconciliation: re-binds per-chunk data to whichever live
      // region owns each chunk's (X, Y, Z) footprint, the single primitive
      // behind load rehydration, the post-flush sweep, and the manual
      // self-test. IChunkOwnerQuery wraps RegionService's footprint lookup.
      Bind<IChunkOwnerQuery>().To<RegionChunkOwnerQuery>().AsSingleton();
      Bind<ChunkReconciler>().AsSingleton();
      Bind<Keystone.Core.Spatial.TileSlotRegistry>().AsSingleton();

      // Map-global per-surface persisted float layers (per-tile
      // counterpart to ChunkValueStore). Mirrors vanilla
      // SoilMoistureSimulator: dense float[] per SurfaceField, indexed
      // by MapIndexService 3D index, terrain-change cleanup via
      // IThreadSafeColumnTerrainMap events, packed persistence. Auto-
      // discovered as ISaveableSingleton/ILoadableSingleton/
      // ITickableSingleton. Holds RiparianMaturity (the sustained-water
      // signal that gates Grassland's riparian flourishes).
      Bind<Keystone.Mod.Surface.SurfaceFieldStore>().AsSingleton();
      // Per-tile riparian dominance inputs (clean-near-water suitability +
      // R), shared by ChunkRulesApplier and the tile debug panel so the
      // read-side "riparian conditions" definition has one owner.
      Bind<Keystone.Mod.Surface.RiparianTileQuery>().AsSingleton();

      // Spatial helpers. Aggregate per-column / per-tile port
      // primitives into neighbourhood predicates (water-edge,
      // future canopy / dryness / etc.) so placement filters and
      // handlers don't reimplement the neighbour walks.
      Bind<WaterProximity>().AsSingleton();
      Bind<CliffProximity>().AsSingleton();

      // Recipe-filter registry + filter implementations. New filters
      // land via MultiBind<IRecipeFilter>; the handlers consult
      // RecipeFilterRegistry by name (per-recipe `Filter` field).
      Bind<RecipeFilterRegistry>().AsSingleton();
      MultiBind<IRecipeFilter>().To<WaterEdgeRecipeFilter>().AsSingleton();
      MultiBind<IRecipeFilter>().To<NearWaterRecipeFilter>().AsSingleton();
      MultiBind<IRecipeFilter>().To<NearShoreRecipeFilter>().AsSingleton();
      MultiBind<IRecipeFilter>().To<RiverBankRecipeFilter>().AsSingleton();
      MultiBind<IRecipeFilter>().To<ContaminatedTileRecipeFilter>().AsSingleton();

      // Class B area query — feeds BuildingDeconstructionClassBPatch
      // (Harmony) the IBlockService it can't take via constructor
      // injection. Publishes itself to a static accessor on Load.
      Bind<ClassBAreaQuery>().AsSingleton();

      // Faction id accessor — gives TemplateCollectionServicePatch and
      // CrossFactionFloraPlacementTool a deterministic FactionService
      // lookup from static context. Replaces a side-channel that
      // coupled patches to cross-faction provider iteration order.
      Bind<FactionIdAccessor>().AsSingleton();

      // Spec service accessor — gives TemplateCollectionServicePatch a
      // way to enumerate TemplateCollectionSpec instances from a
      // static context (collection specs aren't in
      // TemplateCollectionService.AllTemplates, so the patch needs
      // direct ISpecService access).
      Bind<SpecServiceAccessor>().AsSingleton();

      // Natural-reproduction rate accessor — publishes the menu-only
      // KeystoneBaseGameSettings wild-reproduction multiplier to a static
      // field that ReproducibleReproductionChancePatch reads at resource
      // mark-time. ILoadableSingleton so Bindito eagerly constructs it
      // (and the injected settings owner) before any Reproducible marks
      // spots.
      Bind<NaturalReproductionRateAccessor>().AsSingleton();

      // Biome values (per-chunk, time-accumulating). Cross-chunk
      // stress (Badwater radius) will be added later as a separate
      // mechanism that smooths across chunk borders. Two channels
      // per (chunk, biome): short-term Suitability (hour-scale drift
      // toward a stress-aware target, clamped [0,1]) and long-term
      // Maturity (day-scale integration, units of game-days,
      // asymmetric decay by biome polarity).
      Bind<BiomeSuitabilityUpdater>().AsSingleton();
      Bind<BiomeMaturityUpdater>().AsSingleton();

      // Biome level system: per-biome ladder of (lower, upper)
      // maturity ranges. Populated at PostLoad from
      // KeystoneBiomeLevelsSpec instances (default ladder + per-biome
      // overrides). Handlers query the table to compute per-tile
      // progress for action firing.
      Bind<BiomeLevelTable>().AsSingleton();
      Bind<BiomeLevelCatalog>().AsSingleton();

      // Mod-side singletons.
      Bind<KeystoneAssetService>().AsSingleton();
      Bind<PerfTracker>().AsSingleton();
      // Core-side abstraction over PerfTracker; lets Core types
      // (ChunkClusterIndex sub-stage timers, RollingSweep base) take
      // the minimal interface rather than the concrete Mod type.
      Bind<Keystone.Core.Diagnostics.IPerfScope>().ToExisting<PerfTracker>();
      Bind<GameTickCounter>().AsSingleton();
      Bind<KeystoneSurveyor>().AsSingleton();
      Bind<RegionUpdater>().AsSingleton();
      Bind<KeystoneTilePanelActivity>().AsSingleton();
      Bind<KeystoneChunkPanelActivity>().AsSingleton();
      Bind<KeystoneTileDebugPanel>().AsSingleton();
      Bind<KeystoneChunkDebugPanel>().AsSingleton();
      Bind<KeystonePerfWindow>().AsSingleton();
      Bind<EngineTickProbe>().AsSingleton();
      Bind<MeshPathProbe>().AsSingleton();

      // Startup self-check report. The reporter runs all bound
      // IStartupCheck instances at PostLoad, builds an aggregated
      // findings list, and shows it via DialogBoxShower. AlwaysShow
      // is wired to KeystoneDevMode.IsEnabled -- on for the local
      // dev deploy, off for release.
      Bind<StartupReporter>().AsSingleton();
      // Aggregator subsystems record into. Bound before any startup
      // check so the IntegrationHealthCheck can read from it.
      Bind<KeystoneIntegrationHealth>().AsSingleton();
      MultiBind<IStartupCheck>().To<HarmonyStartupCheck>().AsSingleton();
      MultiBind<IStartupCheck>().To<CatalogStartupCheck>().AsSingleton();
      MultiBind<IStartupCheck>().To<BlueprintResolutionCheck>().AsSingleton();
      MultiBind<IStartupCheck>().To<SurveyStartupCheck>().AsSingleton();
      MultiBind<IStartupCheck>().To<SnapshotStartupCheck>().AsSingleton();
      MultiBind<IStartupCheck>().To<IntegrationHealthCheck>().AsSingleton();

      // Developer-facing self-test battery. Runs only on a manual
      // click in the Test tab of KeystonePerfWindow; distinct from
      // IStartupCheck (player-facing environment checks). The runner
      // collects every bound IKeystoneSelfTest and renders the
      // aggregated report into the tab. See the project memory note
      // "project-two-test-systems" for the rationale on keeping the
      // two systems separate.
      Bind<SelfTestRunner>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<NatureBuildingWiringTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<EcologyTransparentBuildingWiringTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<EcologyNoAuraBuildingWiringTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<SpecShapeTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<PortAdapterSanityTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<FaunaAnimatorResolutionTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<RecipeBookValidityTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<DecoratorCoverageTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<LoaderSurvivalTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<PatchScopeInvariantTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<WildReproductionThrottleTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<PipelineHealthTest>().AsSingleton();
      MultiBind<IKeystoneSelfTest>().To<ChunkReconciliationSelfTest>().AsSingleton();

      // IKeystoneLoadStatus multi-bind: every loader whose successful
      // initialisation the LoaderSurvivalTest should verify. Add new
      // loaders here when they're added to the configurator; see
      // IKeystoneLoadStatus's docstring for the contract.
      MultiBind<IKeystoneLoadStatus>().To<BlueprintResolver>().AsSingleton();
      MultiBind<IKeystoneLoadStatus>().To<BuildingCatalogLoader>().AsSingleton();
      MultiBind<IKeystoneLoadStatus>().To<FloraCatalogLoader>().AsSingleton();
      MultiBind<IKeystoneLoadStatus>().To<KeystonePersistence>().AsSingleton();
      // Auto-spawning probes -- left unbound now that we have the
      // FlourishPlacementTool for on-demand iteration. Re-enable
      // individually when reproducing a specific finding.
      // Bind<KeystoneSpawnProbe>().AsSingleton();
      // Bind<PassiveObjectProbe>().AsSingleton();
      // Bind<ParticleProbe>().AsSingleton();
      // Bind<StrippedEntityProbe>().AsSingleton();
      // Bind<BlockingCandidateProbe>().AsSingleton();
      // DerivedBlueprintProbe stays unbound: even no-op spec
      // replacement via ctor #2 hits InvalidCastException in the
      // per-instance component cache when two Blueprint objects share
      // a name. Distinct, separate problem from the children one.
      // Bind<DerivedBlueprintProbe>().AsSingleton();
      MultiBind<ITemplateCollectionIdProvider>().To<CrossFactionCollectionProvider>().AsSingleton();
      MultiBind<ITemplateCollectionIdProvider>().To<KeystoneNaturalResourceCollectionProvider>().AsSingleton();
      MultiBind<ITemplateCollectionIdProvider>().To<KeystoneFaunaCollectionProvider>().AsSingleton();
      MultiBind<IMaterialCollectionIdsProvider>().To<CrossFactionMaterialProvider>().AsSingleton();
      MultiBind<IMaterialCollectionIdsProvider>().To<KeystoneMaterialProvider>().AsSingleton();

      // Per-entity components attached via spec decoration on
      // Keystone-spawnable blueprints:
      //  - KeystoneFlourish: visual lifecycle (phase x life-status x
      //    health), driven by Watered/Floodable specs. Attached
      //    when the blueprint carries KeystoneFlourishSpec.
      //  - KeystoneVariant: persistent class designation ("A" / "B"
      //    / "C") set by the spawning handler. Attached when the
      //    blueprint carries KeystoneVariantSpec; required for
      //    Harmony selection-suppression to apply on save/load.
      //  - KeystoneBiomeLevels: no-op marker, paired with the spec
      //    that carries the per-biome level table data.
      Bind<KeystoneFlourish>().AsTransient();
      Bind<KeystoneVariant>().AsTransient();
      Bind<KeystoneBiomeLevels>().AsTransient();
      Bind<KeystoneDryNaturalResource>().AsTransient();
      Bind<KeystoneRockTint>().AsTransient();
      Bind<Keystone.Mod.Wellbeing.KeystoneNatureSource>().AsTransient();
      Bind<Keystone.Mod.Wellbeing.KeystoneNatureSourceDescriber>().AsTransient();
      Bind<KeystoneGrowthBonus>().AsTransient();
      Bind<KeystoneGrowthBonusFragment>().AsSingleton();
      MultiBind<EntityPanelModule>().ToProvider<GrowthBonusFragmentProvider>().AsSingleton();
      // Keystone's IBlueprintModifierProvider — injects the Nature
      // integration (NeedCollection appends + per-building source spec)
      // into faction blueprints at SpecService load time. Driven by
      // the KeystoneNatureFactions table; replaces the per-faction
      // .optional.blueprint.json overlays. Concrete-then-ToExisting so
      // SpecService picks it up via the IBlueprintModifierProvider
      // multibind (matches Timberborn's own pattern in
      // GameFactionSystemConfigurator).
      Bind<Keystone.Mod.Wellbeing.KeystoneNatureModifierProvider>().AsSingleton();
      MultiBind<IBlueprintModifierProvider>().ToExisting<Keystone.Mod.Wellbeing.KeystoneNatureModifierProvider>();
      Bind<FlourishPlacementTool>().AsSingleton();

      // Class A -- Decoration: non-BlockObject, non-persisted visuals.
      // Reactivity is opt-in per decoration (orthogonal to the class).
      // The registry singleton owns the live set and ticks any
      // attached controllers. Two demo tools: a reactive plant
      // (Dandelion + moisture controller -- Class A with controller)
      // and a passive particle decoration (runtime-built
      // ParticleSystem -- Class A without controller).
      Bind<KeystoneDecorationRegistry>().AsSingleton();
      Bind<DecorationPlacementTool>().AsSingleton();
      Bind<ParticlePlacementTool>().AsSingleton();
      Bind<RockPlacementTool>().AsSingleton();
      Bind<RockClusterPlacementTool>().AsSingleton();
      Bind<Keystone.Mod.Fauna.FaunaPlacementTool>().AsSingleton();
      Bind<Keystone.Mod.Fauna.FishSmokeTestTool>().AsSingleton();
      Bind<Keystone.Mod.Fauna.KeystoneFaunaAnimator>().AsTransient();
      Bind<Keystone.Mod.Fauna.KeystoneFaunaAgent>().AsTransient();
      Bind<Keystone.Mod.Fauna.KeystoneAquaticAgent>().AsTransient();
      Bind<Keystone.Mod.Fauna.KeystoneFaunaRegistry>().AsSingleton();
      // FaunaSpawnQueue: shared FIFO worklist of clusters with
      // outstanding spawn deficit. Written by the cycle ticker, read
      // by the per-frame drainer.
      Bind<Keystone.Mod.Fauna.FaunaSpawnQueue>().AsSingleton();
      Bind<Keystone.Mod.Fauna.FaunaCycleTicker>().AsSingleton();
      // FaunaCycleTicker is ILoadableSingleton (for EventBus
      // EntityDeleted subscription) and ITickableSingleton (auto-
      // discovered by the singleton repository, drives the rolling
      // capacity-reconciliation sweep — enqueues deficit clusters
      // for the drainer, culls surpluses immediately).
      MultiBind<Timberborn.SingletonSystem.ILoadableSingleton>()
          .ToExisting<Keystone.Mod.Fauna.FaunaCycleTicker>();
      // FaunaSpawnDrainer: per-frame execution side of the spawn
      // pipeline. IUpdatableSingleton (auto-discovered) so it ticks
      // during pause too — a long fast-forward followed by a pause
      // produces visible population convergence right as the player
      // slows down. Frustum-gated to hide the visible pop.
      Bind<Keystone.Mod.Fauna.FaunaSpawnDrainer>().AsSingleton();
      Bind<Keystone.Mod.Fauna.FaunaTopologyChangeWatcher>().AsSingleton();
      // FaunaTopologyChangeWatcher subscribes to BlockObjectSetEvent +
      // ITerrainService.TerrainHeightChanged at Load(), so it has to
      // be registered as an ILoadableSingleton.
      MultiBind<Timberborn.SingletonSystem.ILoadableSingleton>()
          .ToExisting<Keystone.Mod.Fauna.FaunaTopologyChangeWatcher>();
      Bind<Keystone.Core.Ports.IRegionTopologyQuery>().To<Keystone.Mod.Adapters.RegionTopologyAdapter>().AsSingleton();

      // Atmospheric directors: time-of-day-driven Class A ephemera that
      // don't fit the recipe layer (which is biome-keyed and per-day,
      // not choreographed across an in-game morning).
      Bind<Atmosphere.WetlandMistDirector>().AsSingleton();

      // Activity-panel recorder. Pull model: nothing happens unless
      // the panel calls TakeSnapshot, so the singleton's existence
      // costs only the DI graph entries.
      Bind<Diagnostics.KeystoneActivityRecorder>().AsSingleton();

      // Class D -- vanilla natural-resource placement, dev tools.
      // VanillaFloraPlacementTool spawns a donor available to the
      // active faction natively; CrossFactionFloraPlacementTool spawns
      // a faction-incompatible donor (cross-faction, via
      // CrossFactionCollectionProvider's loaded templates). Both are
      // Class D in the content taxonomy; the faction split is a build-
      // time concern, not a content-design distinction.
      Bind<CrossFactionFloraPlacementTool>().AsSingleton();
      Bind<VanillaFloraPlacementTool>().AsSingleton();
      Bind<StumpPlacementTool>().AsSingleton();

      // Toolbar group hosting all four dev tools under a single
      // expandable button.
      Bind<KeystoneToolGroup>().AsSingleton();

      // Player-facing mixed-planting brush -- a Keystone reimplementation
      // of the third-party "Forest Tool" concept (drag-select an area, queue
      // a random mix of plantable species through the vanilla planting
      // pipeline), extended to crops. Two category variants share
      // KeystonePlantingToolBase + the Core PlantingPalette policy; the menu
      // initializer injects their buttons into the vanilla "Fields" / "Forestry"
      // planting menus. NOT dev-gated (unlike KeystoneToolGroup). The crop
      // variant ships live; the trees/bushes variant is built and bound but
      // its button stays unwired (KeystonePlantingMenuInitializer.EnableForestVariant)
      // until we've squared the overlap with Forest Tool's author. See
      // docs/private/foresttool.md and src/Keystone.Mod/Planting/README.md.
      Bind<KeystoneCropPlantingTool>().AsSingleton();
      Bind<KeystoneForestPlantingTool>().AsSingleton();
      Bind<KeystonePlantingMenuInitializer>().AsSingleton();

      MultiBind<TemplateModule>().ToProvider<KeystoneTemplateModuleProvider>().AsSingleton();
      MultiBind<BottomBarModule>().ToProvider<KeystoneBottomBarModuleProvider>().AsSingleton();
      Bind<PlateauHighlighter>().AsSingleton();
      Bind<BiomeOverlayToggle>().AsSingleton();
      Bind<BiomeOverlayRenderer>().AsSingleton();
      Bind<BiomeOverlayLegend>().AsSingleton();
      Bind<KeystoneVisibilityHider>().AsSingleton();
      Bind<FloraCatalogLoader>().AsSingleton();
      Bind<BuildingCatalogLoader>().AsSingleton();
      Bind<EcologyFieldUpdater>().AsSingleton();
      Bind<IEcologyFieldQuery>().ToExisting<EcologyFieldUpdater>();
      Bind<KeystonePersistence>().AsSingleton();
      Bind<RegionValueTicker>().AsSingleton();
      Bind<RegionValueLifecycleHandler>().AsSingleton();

      // Biome ticker -- wires the per-chunk biome scoring into the
      // game's tick loop. Adapter translates ecology-field channels
      // into ChunkBiomeInputs each tick.
      Bind<ChunkBiomeAdapter>().AsSingleton();
      Bind<ChunkBiomeTicker>().AsSingleton();
      Bind<Keystone.Core.Ecology.Clusters.ChunkClusterIndex>().AsSingleton();
      // Cluster ticker -- drives ChunkClusterIndex's incremental
      // rebuild on the same 1 game-hour cadence as ChunkBiomeTicker.
      // Separate from ChunkBiomeTicker so the cluster rebuild doesn't
      // spike a single frame at hour-end; it folds one region per
      // ProcessUnit and swaps the shadow snapshot in at CommitRebuild.
      Bind<ChunkClusterTicker>().AsSingleton();

      // Startup warmup: drain the field updater + biome ticker once
      // synchronously at PostLoad so the biome state is consistent
      // immediately rather than after ~2 game-hours of amortised
      // rolling-sweep ticks.
      Bind<KeystoneStartupWarmup>().AsSingleton();

      // Flourish recipes. FlourishCatalog merges blueprint-discovered
      // recipes (KeystoneRecipeBookSpec entries) with code-registered
      // ClassXRecipe multibindings at PostLoad and indexes them by
      // (biome, level) for the rule handlers. BlueprintResolver caches
      // the BlockObjectSpec walk shared across B/C/D handlers.
      Bind<BlueprintResolver>().AsSingleton();
      Bind<FlourishCatalog>().AsSingleton();

      // Per-class rule handlers. Each handler implements IRuleHandler
      // and is invoked by ChunkRulesApplier during the per-chunk pass.
      // Class A reconciles every cycle (per-cycle deterministic
      // regeneration), Class B is one-shot persistent inert flourish,
      // Class C is persistent-but-respawnable selectable flourish (no
      // memo, retries free tiles), Class D is one-shot vanilla flora
      // (memo'd; vanilla reproduction handles natural regrowth).
      // ClassDSpawnHandler is also bound by concrete type because
      // VanillaFloraPlacementTool calls TrySpawnClassD on it.
      Bind<ClassASpawnHandler>().AsSingleton();
      Bind<ClassBSpawnHandler>().AsSingleton();
      Bind<ClassCSpawnHandler>().AsSingleton();
      Bind<ClassDSpawnHandler>().AsSingleton();
      Bind<AttritionHandler>().AsSingleton();
      MultiBind<IRuleHandler>().ToExisting<ClassASpawnHandler>();
      MultiBind<IRuleHandler>().ToExisting<ClassBSpawnHandler>();
      MultiBind<IRuleHandler>().ToExisting<ClassCSpawnHandler>();
      MultiBind<IRuleHandler>().ToExisting<ClassDSpawnHandler>();
      MultiBind<IRuleHandler>().ToExisting<AttritionHandler>();

      // Chunk-driven rules scheduler. The single per-cycle entry point
      // that visits every (region, chunk) pair in randomised order,
      // determines biome+level once per chunk, and dispatches to the
      // bound rule handlers. Replaces the four prior per-class
      // RollingSweepTicker reconcilers, each of which had its own tile
      // sweep.
      Bind<ChunkRulesApplier>().AsSingleton();

      // Dead-flourish decay sweep. ITickableSingleton (auto-discovered
      // by the singleton repository, like ChunkRulesApplier above):
      // once per game-day it rolls every Dead flourish for deletion at
      // ~10% so dead remains rot away instead of persisting forever on
      // land that never recovers. Reuses EntityComponentRegistry +
      // KeystoneFlourish.CurrentLifeStatus; no parallel tracking.
      Bind<Keystone.Mod.Flourish.KeystoneFlourishDecayTicker>().AsSingleton();
    }

    /// <summary>
    /// Provides Keystone's contribution to the global TemplateModule.
    /// Wires the <see cref="KeystoneFlourishSpec"/> marker spec to its
    /// decorator (<see cref="KeystoneFlourish"/>). Add more
    /// <c>AddDecorator</c> calls here as we expand the marker-and-
    /// behavior pairs (per-flora ecology stress, fauna components, etc.).
    /// </summary>
    private class KeystoneTemplateModuleProvider : IProvider<TemplateModule> {
      public TemplateModule Get() {
        var builder = new TemplateModule.Builder();
        // Visual lifecycle (phase x life-status x health) on any
        // blueprint that opts in via KeystoneFlourishSpec.
        builder.AddDecorator<KeystoneFlourishSpec, KeystoneFlourish>();
        // Per-entity persistent class designation. Handlers stamp
        // this at spawn time; Harmony selection-suppression patches
        // gate on Class == "B".
        builder.AddDecorator<KeystoneVariantSpec, KeystoneVariant>();
        // KeystoneBiomeLevels: the spec carries per-biome level
        // ranges; the catalog drains it into BiomeLevelTable.
        builder.AddDecorator<KeystoneBiomeLevelsSpec, KeystoneBiomeLevels>();
        // KeystoneDryNaturalResource: habitat marker for dry-loving
        // flourishes (Dry biome content). Marker only — no runtime
        // behaviour today; see the spec's docstring for forward-
        // compatibility notes.
        builder.AddDecorator<KeystoneDryNaturalResourceSpec, KeystoneDryNaturalResource>();
        // KeystoneRockTint: runtime biome-driven material swapping
        // for inanimate rock clusters (Class C). Attaches the
        // per-entity behaviour that registers with
        // KeystoneRockTintService for the per-cycle re-tint.
        builder.AddDecorator<KeystoneRockTintSpec, KeystoneRockTint>();
        // KeystoneFaunaAnimator: drives the IAnimator on a fauna
        // prefab (VAT-driven Idle loop) and keeps animator Speed in
        // sync with the game-speed slider via CurrentSpeedChangedEvent.
        builder.AddDecorator<Keystone.Mod.Fauna.KeystoneFaunaAnimatorSpec, Keystone.Mod.Fauna.KeystoneFaunaAnimator>();
        // KeystoneFaunaAgent: wander-and-idle state machine for fauna.
        // Attached at prefab-build time with Bindito-injected services
        // (RegionService, IRegionTopologyQuery). Runtime state (animator
        // handle, region, tile) is wired in by FaunaPlacementTool via
        // KeystoneFaunaAgent.Configure on each spawned instance.
        builder.AddDecorator<Keystone.Mod.Fauna.KeystoneFaunaAgentSpec, Keystone.Mod.Fauna.KeystoneFaunaAgent>();
        // KeystoneAquaticAgent: continuous-swim agent for fish. Parallel
        // to the land agent but with water-tile + biome-set walkability
        // and no idle state. Configure is called by FishSmokeTestTool
        // on each spawned instance.
        builder.AddDecorator<Keystone.Mod.Fauna.KeystoneAquaticAgentSpec, Keystone.Mod.Fauna.KeystoneAquaticAgent>();
        // KeystoneNatureSource: per-building tickable that satisfies a
        // biome-keyed Nature need on visiting beavers, scaled by local
        // chunk Maturity. See KeystoneNatureSourceSpec docstring.
        builder.AddDecorator<Keystone.Mod.Wellbeing.KeystoneNatureSourceSpec, Keystone.Mod.Wellbeing.KeystoneNatureSource>();
        // KeystoneGrowthBonus: biome-driven growth-speed bonus for
        // natural-resource plants. Attached to all GrowableSpec entities;
        // the component self-filters at startup (NaturalResource + not
        // Crop + matching target biome).
        builder.AddDecorator<GrowableSpec, KeystoneGrowthBonus>();
        // KeystoneNatureSourceDescriber: IEntityDescriber so the build-
        // menu tooltip + placed-entity panel report which Nature needs
        // the building can satisfy (and the current live rate once
        // placed). Without this the player has no UI surface telling
        // them the building does anything Nature-related.
        builder.AddDecorator<Keystone.Mod.Wellbeing.KeystoneNatureSourceSpec, Keystone.Mod.Wellbeing.KeystoneNatureSourceDescriber>();
        return builder.Build();
      }
    }

    /// <summary>
    /// Provides Keystone's contribution to the bottom toolbar. Hosts
    /// the <see cref="KeystoneToolGroup"/> -- a single expandable
    /// button containing one sub-button per content class (A/B/C/D).
    /// </summary>
    private class KeystoneBottomBarModuleProvider : IProvider<BottomBarModule> {
      private readonly KeystoneToolGroup _toolGroup;
      public KeystoneBottomBarModuleProvider(KeystoneToolGroup toolGroup) {
        _toolGroup = toolGroup;
      }
      public BottomBarModule Get() {
        var builder = new BottomBarModule.Builder();
        builder.AddLeftSectionElement(_toolGroup, 200);
        return builder.Build();
      }
    }

    private class GrowthBonusFragmentProvider : IProvider<EntityPanelModule> {
      private readonly KeystoneGrowthBonusFragment _fragment;
      public GrowthBonusFragmentProvider(KeystoneGrowthBonusFragment fragment) {
        _fragment = fragment;
      }
      public EntityPanelModule Get() {
        var builder = new EntityPanelModule.Builder();
        builder.AddBottomFragment(_fragment);
        return builder.Build();
      }
    }

  }

}
