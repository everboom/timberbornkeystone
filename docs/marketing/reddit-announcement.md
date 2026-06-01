# Keystone — marketing copy (v1.0)

> Working draft. Reusable across Steam Workshop description, mod.io page, and the Reddit/Discord launch post.
> Sections below are modular — pull the hook + features for a short post, add FAQ + compatibility for a full store page.
> Blockquoted `[CONFIRM]` markers are facts I couldn't verify from the code — fill or correct before publishing.

---

## Hook (short version)

**Keystone — an ecology layer for Timberborn.**

Beavers are nature's keystone species. Keystone makes that real: the water and land you shape decide what can live there, and the world answers — biomes form, plants spread, animals arrive, and poisoned ground scars over.

Nothing is painted on the map. A simulation reads every tile of your terrain — water depth and flow, soil moisture, contamination, what you've planted — and biomes emerge from it. Healthy land builds slowly over many days; neglect heals; damage leaves a mark long after the source is gone.

---

## What Keystone does

Keystone rewards good stewardship of the world. Bring water across the map, plant variety, clean up contamination, and let land mature — and the game pays you back three ways:

### A more beautiful world
Decorations and flourishes fill in healthy ground — reeds and cattails in slow side channels, scrub where it stays dry, plants along grassland banks. More trees and greenery spread naturally, including flora normally locked to the other faction. Wildlife arrives where the habitat can support it: deer in mature forest, cattle in established grassland, fish threading through wetland and river water. Let a biome collapse and it all fades — flourishes wilt, animals leave — so the map shows your mistakes as well as your wins.

### Faster growth where the land is healthy
Plants grow faster when the land around them is the healthy biome they belong to. Variety pays, too — a dense single-crop field reads as *monoculture* and earns little, so diverse planting is what really rewards you. Stewardship becomes a production advantage, not just a pretty one.

### Happier beavers
A new "Nature" need rewards beavers who relax near thriving biomes — fulfilled at each faction's *own existing* contemplation buildings (shrines, terraces, campfires, lidos, and the like). No new buildings to learn; the places your beavers already visit simply mean more next to healthy land. (See Compatibility for exactly which factions and buildings.)

### Under it all: emergent biomes
Nothing is painted on the map. A simulation reads every part of your terrain — water depth and flow, soil moisture, contamination, what you've planted — and biomes form themselves: wetland from slow shallow water, forest from mixed trees, grassland from open irrigated land, and so on. Conditions held over many in-game days *mature* into richer content; neglect rolls it back; recovery is slower than ruin. Badwater and contamination leave scars that stay barren for in-game weeks after you remove the source — cleanup is a real project, not a click.

---

## Performance

Ecology is heavy to simulate, so Keystone is built not to cost you framerate. Work is multi-threaded and spread out over long in-game periods rather than done all at once — the mod sips a little each tick instead of stalling on big recalculations. The goal is a steady frame rate with no hitches or spikes, even on large maps at high game speed.

---

## Quality of life

- **Mod settings menu.** Throttle individual features to taste — dial things back for performance on lower-end machines, or tune the balance between realism and convenience.
- **Biome overlay.** A built-in overlay shows what the mod sees: which biomes it reads on your terrain and how it's interpreting the world. Handy for understanding why a flourish appeared, why an animal won't settle, or how close a patch is to maturing.

---

## FAQ

**Do I need to start a new game?**
No. Add Keystone to a fresh settlement or to a save you're already playing — it reads your current terrain and the ecology builds from there.

**Can I add and remove it freely?**
Yes — it's fully reversible, so there's no commitment in trying it. Removing Keystone cleanly deletes everything it added and you can keep playing the same save; add it back later and it simply starts over. Add it mid-playthrough, take it out, put it back — it just works.

**Will it change how my map looks immediately?**
No. Ecology accumulates over in-game days. Freshly irrigated land won't sprout a mature forest overnight — that's the point. You'll see flourishes and wildlife appear as conditions hold.

**Is it just visual, or does it affect gameplay?**
It's mainly visual feedback — a world that visibly responds to your stewardship — but it touches gameplay in two ways too: plants grow faster near the healthy biomes they belong to, and beavers gain an optional Nature wellbeing need they fulfill near thriving land. No yield penalties, raids, or disease.

**Does it hurt performance?**
No — see Performance above. Work is multi-threaded and spread over time to avoid frame spikes, and you can throttle features in the mod settings if you're on lower-end hardware.

**Which faction should I play?**
Both Folktails and Iron Teeth get the full ecology simulation, biomes, flourishes, and wildlife. The beaver Nature-need bonus differs by faction — see Compatibility.

---

## Compatibility

**Game version:** Timberborn 1.0+.

**Required mods** (installed automatically as Workshop dependencies):
- TimberUi
- Harmony
- Mod Settings

**Factions:**
- **Full ecology** (biomes, flourishes, wildlife, cross-faction flora): all factions.
- **Beaver "Nature" wellbeing needs:** Folktails, Leaf Coats, and Emberpelts.
  - Three biome-flavored needs — Forest (+3), Wetland (+3), Grassland (+1) favorable wellbeing.
  - Satisfied at contemplation-style buildings near healthy biomes (shrines/contemplation spots, rooftop terraces, campfires, gardens, lidos / mud pits, etc.), with each building's eligible biomes set by its theme.
  - **Emberpelts** get Forest + Grassland only — lore-driven, they dislike water, so Wetland is dropped.
  - **Iron Teeth** are intentionally excluded from the Nature needs — their leisure buildings are industrial by design, so the "contemplation in nature" mechanic doesn't fit. IT still gets the complete ecology layer; this is a deliberate asymmetry, not a gap.

**Other mods:** Keystone is a standard Harmony mod and coexists with most.
> [CONFIRM] Known caution worth surfacing for mod-savvy players: Keystone patches some of the same template/selection systems other content mods touch. Decide whether to name any specific known-good or known-conflicting mods (e.g. ModdableTimberborn shares the template-patching surface). Community faction mods that add a contemplation building + a `NeedCollection` can be wired into the Nature system on request.

**Multiplayer:** N/A — Timberborn is single-player.

---

## Links

- 🔗 Steam Workshop: [CONFIRM URL]
- 💬 Discord: [CONFIRM URL]
- 📦 mod.io: [CONFIRM — planned?]
