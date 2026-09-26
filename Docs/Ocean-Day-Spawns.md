# Ocean spawns by voyage day

Edit `Assets/_Project/Resources/OceanGeneration.asset`:

- **Floating Objects**: the single pool of all floating item definitions and their relative **Weight**. There are no per-item quantity fields. **Floating Loot Per Ship**, **Loot Per Interval** and **Loot Interval** control the overall population and spawning rate.
- **Floating Rarity Unlocks**: each day entry contains **From Day** and a **Rarities** list. Add multiple entries to Rarities and select their Rarity enums. Every listed tier becomes available from this day onward. For example, one day-1 entry lists Common and Uncommon; day 2 lists only Rare, day 3 only Epic, and day 4 only Legendary. Earlier tiers stay unlocked automatically.
- **Buried Chests**: the single pool of chest item definitions, relative **Weight**, **Minimum Amount** and **Maximum Amount**. Amounts select how many copies of the chosen chest type to place before another weighted selection, limited by the island's total **Chest Count Small/Medium/Large**. Burial depth and total chest count settings remain unchanged.
- **Buried Chest Rarity Unlocks**: cumulative rarity unlocks for the chest pool, independent from floating items.
- **Enemy Spawn Days**: add a day and its **Enemies**, then choose **Enemy Type** in each entry. **Random** selects one valid listed entry per island spawn point. With Random off, every listed entry spawns a group. Group counts, spawn radii, health bars and combat settings still come from the matching **Enemy Spawn Point Prefabs**.

**From Day** matches the day on the voyage HUD. Loot rarity unlocks accumulate; a later rule never removes previously unlocked rarities. List order and duplicate unlocks do not change the result. Unlisted rarities never spawn, and an empty unlock list disables its source entirely. Unlocking Rare alone does not implicitly unlock Common or Uncommon.

The world always selects items from the single global pool, ignoring entries whose rarity is still locked or whose weight is zero. Newly added items automatically follow the unlock day of their rarity; they never need to be copied into day lists. The shipped settings use the example unlock schedule above for both water loot and island chests and preserve the original item weights. Chest contents remain controlled by each chest's existing loot table.

Enemy day rules retain their separate behavior: each rule applies until the next configured day and its **Random** flag controls selection. An empty Enemies list means no enemies. If the entire Enemy Spawn Days schedule is empty, the original Enemy Spawn Point Prefabs pool is used.

Water loot uses the current day for every new spawn. Buried chests use the day when an island is populated, including preloaded islands. Island enemies use the day when players activate a spawn point. Existing items, chests and enemies remain as they were; changing day does not replace or duplicate them. Night-wave spawning remains separate, and new ocean loot/chest spawning remains paused during a battle.

## Chest contents

Edit `Assets/_Project/Data/Ocean/ChestLoot.asset`:

- **Loot Pool** is the shared list of every possible reward. Add each item once and set its **Weight**, **Minimum Amount** and **Maximum Amount**.
- **Tiers** contains the chest profiles. **Tier** is the rarity of the chest item, while **Allowed Rarities** is the list of reward rarities that this chest may drop. Select multiple rarities in one profile as needed.
- **Minimum Rolls / Maximum Rolls** determine how many weighted selections the chest makes. Each selection uses the chosen shared entry's amount range. Existing luck bonuses and reward caps remain in place.
- **Matching Rarity Percent** reserves at least this percentage of rolls for the chest's own rarity (65% by default, rounded up). These rolls choose items by their shared weights. The remaining rolls select other allowed rarities. If large stacks would outweigh matching items, additional rolls switch to matching rewards, using those entries' own amount ranges. Matching items are emitted first so the 48-item cap cannot reverse their majority. This guarantee applies only when the chest's own rarity is allowed and has eligible positive-weight rewards; otherwise all rolls use the allowed pool normally.

For example, a Common chest can allow Common and Uncommon rewards, while a Rare chest can allow Common, Uncommon and Rare. These are explicit per-chest lists, independent of the ocean's day unlocks: no rarity is implicitly added. Empty Allowed Rarities means no rewards. Luck never unlocks a forbidden rarity, and chest items are excluded from chest contents.

All profiles read the same Loot Pool. Changing an item's weight or amount there affects every chest profile that permits its rarity; adding a new item does not require editing each profile. The existing chest item definitions still reference this same ChestLoot asset.
