# Changelog

## 1.0.0.1

- **Fixed a save that could refuse to load**: if another mod placed a building on the exact tile of a Keystone plant — and the overlapping placement got written into the save anyway — the game would fail to load that save from then on, with an error that never mentioned Keystone. Keystone plants now bow out gracefully when they find themselves on a tile that's no longer valid: the stray plant is quietly removed and the save loads normally.

## 1.0.0.0

- **Keystone reaches 1.0** — a stable-milestone release marking the base mod ready for a 1.0 listing.
- **Biome lens polish**: the cursor overlay's biome markers now float above mist as well as water, and sit at the correct height on the tile.

## 0.6.7.1

- **River plants no longer look dead in the water**: river-bank flourishes now spawn only where the water is about one tile deep — the depth they're meant to live in. Before, they could appear under deeper water (or on dry bank) and immediately render as wilted/dead, because the game reads anything outside that range as flooded or parched.
- **Biome lens reads over water**: the cursor overlay's biome markers now float on top of any water on the tile instead of being hidden under the surface, so you can read biomes on rivers, lakes, and flooded ground.
- **Performance window polish** (Alt+Shift+K): count-based rows are now separated from timing rows so they're no longer mistaken for milliseconds, the frequency column is capped so a burst can't blow out the layout, and a new **Clear** button re-baselines the stats after a save finishes loading — so the one-time load spikes stop skewing what you're watching. More of Keystone's background work is broken out individually, too.

## 0.6.7

- **Biome lens shows established vs. emerging biome**: the cursor overlay now draws the biome that has actually taken hold (by maturity) as the base marker, and — when current conditions favour a *different* biome — floats that one above it. So you can spot where a biome is establishing or giving way at a glance: a forest that just flooded reads as Forest with Wetland rising on top.
- **Smoother terrain edits on developed maps**: placing a building or terraforming in a way that reshapes a large region no longer causes a noticeable hitch. The per-edit ecology bookkeeping that used to spike (and got worse the bigger the map) is now cheap regardless of region or map size.
- **Building classification fix**: the Emberpelts farmhouse now correctly registers as settled infrastructure (it was missing its settlement aura), and building-name matching is case-insensitive so similar mismatches don't recur.

## 0.6.6

- **Crops grow faster in healthy grassland**: land crops now get a growth-speed boost when grown on mature, diverse grassland. A dense single-crop field reads as monoculture and gets little to none — the bonus rewards crop variety and crops grown in genuine grassland rather than a sterile monocrop block.
- **Toxic terrain scars run deeper**: contaminated and badwater ground now hold their toxic state more stubbornly (deeper maturity ceilings — Contaminated and Badwater both raised), so the after-effects of pollution linger longer and take more sustained cleanup to reverse.
- **Stability & correctness fixes**: chunks straddling a region boundary keep their ecology data when one side reshuffles (closing a gap left by the 0.6.5 "maturity stays with the land" work), and an internal change-detection fix ensures per-tile biome state refreshes reliably.

## 0.6.5

- **Biome maturity stays with the land through terrain edits**: digging, building, or anything that reshuffles regions near a boundary no longer wipes accumulated biome maturity in patches. Per-chunk ecology now follows the ground it sits on as regions split, merge, or get reshaped, instead of being stranded under a stale region.
- **Biome lens respects the height slider**: the biome overlay's markers now hide along with terrain you slice away with the vertical-view (cutaway) slider, instead of floating above the cut.
- **Stability & correctness fixes**: closed a region-topology edge case where a single combined terrain edit could leave a region spanning disconnected ground (a save-consistency risk); badwater now reliably reads as dominant on fully-contaminated tiles; new-map ecology seeding no longer overshoots for slow-establishing biomes; and a fresh-load threading issue around riparian seeding was removed.

## 0.3.3.0

- **Smoother gameplay on big maps**: the cluster index rebuild that used to spike at every game-hour boundary is now spread across the hour, and skips regions / chunks no consumer reads from. Maps with extensive dry, contaminated, or badwater terrain see the biggest relief.
- **Steam description updates**: added an FAQ covering performance monitoring (Alt+Shift+K), and how to keep Keystone from spawning ambient flora (planting marks) or blocking plants (cutting marks) where you don't want them.

## 0.3.2.0

- **Cutting marks block regrowth, except inside existing forests**: tiles marked for tree-cutting won't get Keystone-spawned bushes, crops, or ground cover. Trees still regrow there only if an adult cuttable tree is in the 8 surrounding tiles — so selectively harvesting inside a Keystone forest still works, but clearing a wide swath for a building stays cleared as cutting proceeds.

## 0.3.1.0

- **Fauna spawn/despawn pops hidden**: animals now appear and disappear off-screen, never in the camera view.
- **Populations converge at high speed**: the old speed-3 freeze on fauna activity is gone — herds fill in during fast-forward too, and during pause.
- **Stuck-cow fix**: cows and bulls no longer pile onto a single edge tile of a chunk near a region boundary. The spawn picker now uses the same walkability rule the agents themselves obey.
- **Stranded fauna self-despawn**: agents that can't find a path or end up on a tile they can't leave despawn within an in-game hour and respawn somewhere they can actually live.

## 0.3.0.0

- **Ecological cluster score** drives both fauna capacity and Nature wellbeing — one quality×quantity number per cluster.
- **Nature tiers** in tooltips: Minor / Medium / Major / **Pristine**.
- **Fauna rewritten**: continuous rolling-sweep spawn/cull instead of a dawn burst — no more sunrise stutter, populations recover within ~6 game-hours of loading a save.
- **Two-faction support**: Folktails / LeafCoats / Emberpelts each get their own thematic Nature-need bucket on Contemplation Spot, Observation Terrace, and Lido.
- **Debug overlay** (Alt+Shift+K) now shows cluster score, tier histogram, and per-bucket fauna diagnostics.
