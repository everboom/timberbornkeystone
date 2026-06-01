<p align="center">
  <img src="docs/marketing/banner%20with%20logo.png" alt="Keystone — Ecology for Timberborn" width="100%">
</p>

<h1 align="center">Keystone — Ecology for Timberborn</h1>

<p align="center"><i>Beavers are nature's keystone species. Take their role further — caretakers of the land — and the whole map comes back to life.</i></p>

---

Bring water to every corner of the map, and life follows. Cattails fill slow side channels and fish thread through them. Birches line wet banks. Cattle graze established grassland; deer roam mature forest. Rocks pale under sun, darken in the wet. Dry land makes do with thin scrub and tumbleweed until water reaches it. Poisoned ground stays scarred long after the source is gone.

**Nothing is painted on the map and nothing is random.** Underneath, the mod reads every part of your terrain — water, soil, plants, contamination — and the biomes form themselves from what it finds. Mature land takes in-game time to grow. Brief lapses heal. Sustained damage leaves traces.

## Three ways the world pays you back

Keystone rewards good stewardship. Bring water across the map, plant variety, clean up contamination, and let land mature — and the game answers:

- **A more beautiful world.** Decorations and flourishes fill in healthy ground; trees and greenery spread on their own — including flora normally locked to the *other* faction. Wildlife arrives where the habitat supports it. Let a biome collapse and it all fades — so the map shows your mistakes as well as your wins.
- **Faster growth where the land is healthy.** Plants grow faster when the land around them is the healthy biome they belong to. Variety pays: a dense single-crop field reads as *monoculture* and earns little, so diverse planting is what really rewards you.
- **Happier beavers.** Three new "Nature" wellbeing needs reward beavers who relax near thriving biomes — fulfilled at each faction's *own existing* contemplation buildings (shrines, terraces, campfires, lidos, and the like). No new buildings to learn.

### Under it all: emergent biomes

A simulation reads water depth and flow, soil moisture, contamination, and what you've planted, and biomes form themselves: wetland from slow shallow water, forest from mixed trees, grassland from open irrigated land, and so on. Conditions held over many in-game days *mature* into richer content; neglect rolls it back; recovery is slower than ruin. Badwater and contamination leave scars that stay barren for in-game weeks after you remove the source — cleanup is a real project, not a click.

## What it adds

- **Eleven emergent biomes** — forest, grassland, wetland, river, lake, shoreline, cave, monoculture, dry, contaminated, badwater. There's no designation tool; each biome is whatever the map's conditions add up to.
- **Around 70 plant scenes that bloom and decay with the map.** Slow side channels grow spadderdock and cattails. Riverbanks pick up wetland-edge cover. Wet shorelines sprout birches. Drying land sheds its lush plants. Poisoned ground kills what's on it.
- **Stone that responds to its conditions** — rocks go pale and dusty under drought, wet-dark along streams, sickly under badwater.
- **Cross-faction flora.** Plant the right conditions and the *other* faction's flora takes root on its own. No menus, no recipes — just the right ground in the right place.
- **Morning mist over wetlands.** Slow shallow water at the right depth wakes up wreathed in fog, which burns off as the day warms.
- **Long-term ecological memory.** Healthy land builds slowly when conditions hold and fades when they don't. A short lapse heals; long neglect leaves a scar. Drought management becomes biome management.
- **Wildlife where the habitat supports it.** Deer roam mature forest, cattle find their footing on established grassland, fish thread through wetlands and across standing lakes. Populations scale with the biome's reach — a wider block of healthy forest holds more deer; a small one holds none.
- **Beaver wellbeing tied to the ecosystem.** The bigger and healthier the ecosystem near a contemplation building, the stronger the bonus.

## Quality of life

- **Mod settings menu** — throttle individual features to taste: dial things back for performance on lower-end machines, or tune the balance between realism and convenience.
- **Biome overlay** — a built-in overlay shows what the mod sees: which biomes it reads on your terrain and how it's interpreting the world. Handy for understanding why a flourish appeared, why an animal won't settle, or how close a patch is to maturing.

## Performance

Ecology is heavy to simulate, so Keystone is built not to cost you framerate. Work is multi-threaded and spread thinly across many frames rather than ticked in bursts — the mod sips a little each tick instead of stalling on big recalculations. Most setups shouldn't notice. To see for yourself, press **Alt+Shift+K** in-game to open a live performance window that breaks out Keystone's own costs.

## Compatibility

- **Game version:** Timberborn 1.0
- **Required mods** (installed automatically as Workshop dependencies): **TimberUi**, **Harmony**, **Mod Settings**
- **Factions:**
  - **Full ecology** (biomes, flourishes, wildlife, cross-faction flora): **all factions**.
  - **Beaver "Nature" wellbeing needs:** **Folktails, Leaf Coats, and Emberpelts** — three biome-flavored needs: Forest (+3), Wetland (+3), Grassland (+1), satisfied at contemplation-style buildings near healthy biomes.
    - **Emberpelts** get Forest + Grassland only — lore-driven, they dislike water, so Wetland is dropped.
    - **Iron Teeth** are intentionally excluded from the Nature needs — their leisure buildings are industrial by design, so "contemplation in nature" doesn't fit. IT still gets the complete ecology layer; this is a deliberate asymmetry, not a gap.
  - Specific integrations for other faction mods are possible on request.
- **Other mods:** Keystone is a standard Harmony mod and coexists with most.
- **New games:** the ecology starts on day one — the map's current state seeds the world's biome history immediately.
- **Existing saves:** safe to add, but the world has to notice your work. Biomes need in-game time to build up history, so a mid-game install starts quiet and gathers steam over the days that follow.
- **Removing mid-save:** fully reversible — it undoes Keystone's decoration and forgets the world's ecological history. Add it back later and it simply starts over.

## FAQ

**Will it slow my game down?**
Most setups shouldn't notice — Keystone's per-frame work is intentionally spread thinly across many frames rather than ticked in bursts. Press **Alt+Shift+K** in-game for a live performance window that breaks out Keystone's own costs. On lower-end hardware, the mod settings menu lets you throttle individual systems to claw back more.

**Is it just visual, or does it affect gameplay?**
Mainly visual feedback — a world that visibly responds to your stewardship — but it touches gameplay in two ways: plants grow faster near the healthy biomes they belong to, and beavers gain an optional Nature wellbeing need. No yield penalties, raids, or disease.

**How do I keep Keystone's ambient flora off my build site?**
Mark the area for planting (with any crop). Keystone honours planting marks and won't drop flourishes, ground cover, or other ambient flora on tiles you've designated for farming.

**How do I stop trees, berry bushes, and dandelions from spawning where I don't want them?**
Use the vanilla **mark for cutting** tool. Areas designated for cutting won't see new Keystone-driven spawns of blocking plants, so the swath you've earmarked for clearing stays cleared.

## Suggest a feature or report a bug

Keystone's backlog is community-driven through **[GitHub Discussions »](https://github.com/everboom/timberbornkeystone/discussions)**.

- **💡 Have an idea?** A biome, an animal, a plant, a faction integration — [start a discussion in the **Ideas** category](https://github.com/everboom/timberbornkeystone/discussions/new/choose). Say what you'd like to see and why it fits Keystone's "shape the land, and the world answers" loop. **Browse the existing ideas first and upvote the ones you want** — the most-upvoted suggestions rise to the top and steer what gets built next.
- **🐛 Found a bug?** Open a discussion with your Timberborn version, faction, what happened, and (ideally) your `Player.log` (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`).

Ecosystem screenshots and faction-integration requests are always welcome.

## License

[MIT](LICENSE) © 2026 Erik Verboom
