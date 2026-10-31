# Container Sorter — developer notes

For anyone changing the code or reading the log. Player-facing install and usage live in [README](../README.md).

> Language: English only — code comments, docs/ and CHANGELOG.

- Mod version 1.10.0 · game 2.55 · MelonLoader 0.7.1+ (tested on 0.7.3) · ModSettings required at runtime
- Every claim about game members below was checked against the 2.55 dump and the live install; anything unverified says so.
- Source comments and this document are kept in sync through §11; an entry missing there counts as unfinished work.

Companion documents:

| File | What it answers |
| --- | --- |
| [CHANGELOG.md](CHANGELOG.md) | what changed in each version, and why |
| [README.md](../README.md) | what the player sees, installs and configures |

## 1. File map

| File | Type | Role | Entry points |
| --- | --- | --- | --- |
| `ContainerSorterMod.cs` | `MelonMod` | reads settings, polls the sort key, forwards to the transfer, keeps exceptions out of the log flood | `OnInitializeMelon`, `OnUpdate` |
| `ContainerTransfer.cs` | static | move-in direction (backpack → container): panel gate, backpack snapshot, ordering, capacity math, stack merge/split, panel refresh | `Run` |
| `TargetCount.cs` | static | take-out direction (container → backpack): tops the backpack up to the target count | `Fill` |
| `CuringBoxTransfer.cs` | static | move-in direction for the curing box, which is not a `Container` and opens its own panel | `Run` (returns whether it handled the press) |
| `GearMath.cs` | static | item readings shared by both directions: name normalisation, units, per-unit and total weight, condition, weight tolerance, destroy, unit restore | — |
| `IgnoreList.cs` | static | parses and hot-reloads `Mods\ContainerSorterIgnore.txt` (section markers, count suffix, comments) | `EnsureFile`, `Reload`, `Lookup`, `Targets` |
| `Settings.cs` | `JsonModSettings` | the settings page; the key field is a `KeyCode`, which ModSettings renders as a rebind control | — |

Layering rules:

- One direction per file. `ContainerTransfer` never takes items out; `TargetCount` never puts them in.
- Anything that reads a game member goes through `GearMath`. A second copy of the tolerance or the `(Clone)` normalisation is what makes the two directions disagree.
- The ignore list is parsed in one place. Both directions ask `IgnoreList` for a rule and never re-read the file.

## 2. Per-run pipeline

One key press, one pass. Nothing runs on a timer.

```
OnUpdate (every frame)
└─ debounce 0.25 s
   └─ Pressed(settings.EffectiveKey)        Ctrl gate when RequireCtrl is on
      └─ ContainerTransfer.Run(log, cfg)
         ├─ 0  routing: CuringBoxTransfer.Run()  returns true when Panel_CuringStorage is open → stop here
         ├─ 1  gate: Panel_Container exists and IsEnabled()      else silent return
         ├─ 2  gate: panel.m_Container, Inventory                else warn and return
         ├─ 3  IgnoreList.Reload()                timestamp check only
         ├─ 4  TargetCount.Fill()                 container → backpack, frees space
         ├─ 5  read capacity / used / remaining  *after* step 4
         │     exit early on capacity <= 0, already full, or empty container
         │     (still refreshes the panels when step 4 moved anything)
         ├─ 6  CollectInventoryItems()            snapshot, *after* step 4
         ├─ 7  PickKeepers()                      which instances stay behind
         ├─ 8  OrderForTransfer()                 only when HeavierFirstWhenEqualCondition
         ├─ 9  per item: ignore rule → keep count → capacity → merge / split / plain move
         └─ 10 RefreshPanels()                    three steps, each in its own try
```

Ordering is part of the contract, not a style choice:

- Step 0 comes first because `Panel_Container` and `Panel_CuringStorage` are siblings (`Panel_AutoReferenced` is their common base), not parent and child. Without the branch, the container gate is simply false while the curing box is open and the press does nothing at all.
- Step 4 must come before step 5. Taking items out can empty a container, and a container that is "already full" is exactly the case where the backpack still needs topping up.
- Step 6 must come after step 4. Units taken back in step 4 have to count against the keep rule; snapshotting earlier makes them look like missing units and the next pass takes them again.
- Steps 9 and 4 read the same rule set. A rule that is bidirectional is applied twice per press by design: once outward, once inward.

## 3. Injection points

This mod installs **no Harmony patches** on 2.55. It observes the game from the loader's frame callback and drives the game through public container and inventory calls, which is why a failed container panel leaves nothing patched behind.

| Target | Timing | What it does | Status on 2.55 |
| --- | --- | --- | --- |
| `MelonMod.OnInitializeMelon` | once, at load | create `Settings`, `AddToModSettings`, normalise the key, `IgnoreList.EnsureFile` | used |
| `MelonMod.OnUpdate` | every frame | debounce, read the key, call `ContainerTransfer.Run`, rate-limit errors | used |
| `Panel_Container.IsEnabled` | inside a run | the gate that keeps the mod inert outside a container | used |
| `UnityEngine.Input.GetKeyDown` | inside a run | key polling | used; `InputManager.GetKeyDown` is not usable (see §4.1) |
| `Container.AddGear` / `RemoveGear` | inside a run | the actual transfer | used |
| `Panel_Container.RefreshTables` / `ForceRefresh` | end of a run | rebuild the visible lists | used |
| `Panel_Inventory.RefreshTable` | end of a run | refresh the inventory panel when it is open | used, optional |
| `Panel_CuringStorage.IsEnabled` | inside a run | the routing gate for the curing box | used |
| `CuringStorage.AddItem` / `Inventory.RemoveGear(gameObject, true)` | inside a curing-box run | the actual transfer, in vanilla's order | used |
| `Panel_CuringStorage.ForceRefreshTables` | end of a curing-box run | rebuild both of its tables | used |

No prefixes, postfixes or transpilers exist in this repository. If a future game version makes a polling approach impractical, the entry point to replace is `ContainerTransfer.Run`, not the patches.

## 4. Data and fields

### 4.1 Game side (verified against the 2.55 dump unless noted)

| Member | Type | Why it matters here |
| --- | --- | --- |
| `Container.m_Capacity`, `Container.GetTotalWeightKG()` | `Il2CppTLD.IntBackedUnit.ItemWeight` | integer-backed, **no `ToKilograms`**; divide by `ItemWeight.FromKilograms(1f)` |
| `Container.m_Items` | `List<GearItemObject>` | the only list walked when collecting kinds |
| `Container.AddGear(GearItem)` | void | does **not** merge stacks; merging is done by `MergeInto` before this call |
| `Container.RemoveGear(GearItem)` / `RemoveGear(GearItem, bool toInventory)` | void | the `toInventory` overload is the vanilla "take out" path; whether it really hands the item to the backpack is not visible in IL2CPP, so `TargetCount` verifies the landing |
| `Inventory.m_Items` | `List<GearItemObject>` | preferred over `GetGearItems(Predicate<T>)`, which drags in Il2Cpp generic delegates |
| `Inventory.RemoveGear(GameObject, bool)` / `AddGear(GearItem, bool)` | void | detach before attach; `AddGear` may destroy the object it merged |
| `Inventory.FindByInstanceID(int)`, `GetTotalWeightKG()` | — | landing checks for the take-out direction |
| `GearItem.m_StackableItem.m_Units` | int | unit count of a stack; the single value both directions edit |
| `GearItem.m_StackableItem.m_StackConditionDifferenceConstraint` | float (percent) | condition gap allowed when merging; vanilla leaves 0 in places, treated as 5% |
| `GearItem.InstantiateStackable(int)` | `GearItem` | clones the item without loading its prefab, which avoids the Unity 6 bundle stall |
| `GearItem.GetItemWeightKG(bool)` | `ItemWeight` | whole item (whole stack); divide by units before comparing heaviness |
| `GearItem.GetNormalizedCondition()` | float 0–1 | compared with a 0.001 tolerance so floating-point noise does not swap picks |
| `GearItem.m_InstanceID` | int | identity for landing checks and for stable sorting |
| `GearItem.m_IsHidden`, `m_WaterSupply`, `m_LiquidItem`, `m_InPlayerInventory`, `m_IsInSatchel` | various | exclusion and flag maintenance |
| `GearItem.m_ClothingItem.IsWearing()` | bool | worn clothing is skipped when `SkipEquipped` is on |
| `Panel_Container.m_Container`, `RefreshTables()`, `ForceRefresh()` | — | the open container and its rebuild hooks |
| `Panel_Inventory.RefreshTable()` | — | refreshes the inventory panel's own grid and weight |
| `InterfaceManager.GetPanel<T>()`, `TryGetPanel<T>(out T)` | — | panel access |
| `GameManager.GetInventoryComponent()`, `GetPlayerManagerComponent().m_ItemInHands` | — | backpack and the held item |
| `InputManager.GetKeyDown(context, key)` | — | **not usable**: `m_CurrentContext` is private with no accessor, hence `UnityEngine.Input` |
| `MelonLoader.Utils.MelonEnvironment.ModsDirectory` | string | where the ignore list lives; falls back to the dll folder |
| `Panel_CuringStorage.m_CuringStorage`, `ForceRefreshTables()` | — | the open curing box and its rebuild hook |
| `CuringStorage.m_MaxStorage`, `TotalWeight` | `ItemWeight` | capacity and used weight of the meat side; **salt has its own storage and its own weight**, so it must not be added here |
| `CuringStorage.CuringItems` | `List<CuringItem>` | one entry per stored object; `CuringItem.m_InstantiatedItem` holds the `GearItem` |
| `CuringStorage.AddItem(GearItem)` | void | parents the object under the box and records it; **validates nothing** — no kind check, no capacity check |
| `CuringStorage.RemoveItem(GearItem)` | void | used by the vanilla take-out path only; this mod does not take items out of a box |

### 4.2 Project state

| State | Where | Lifetime | Notes |
| --- | --- | --- | --- |
| `rules` | `IgnoreList` | process | normalised name → rule; includes the `GEAR_`-less aliases |
| `targets` | `IgnoreList` | process | normalised name → units; **no aliases**, otherwise one rule would run twice |
| `ignoreCount`, `keepCount` | `IgnoreList` | process | counters for the load message only |
| `lastWriteUtc`, `loaded`, `dirPath` | `IgnoreList` | process | hot-reload bookkeeping |
| `settings` | `ContainerSorterMod` | process | the `Settings` instance ModSettings owns |
| `lastRunTime`, `errorCount`, `inputUnavailable` | `ContainerSorterMod` | process | debounce and error rate limiting |

Nothing is written to the save. The only file the mod owns is `Mods\ContainerSorterIgnore.txt` plus the ModSettings JSON.

## 5. Target-count model

A list entry written as `ID*N` means "the backpack should hold exactly N units of this kind". Both directions derive from that one number:

| Backpack units `c` | Who acts | What happens |
| --- | --- | --- |
| `c < N` | `TargetCount.Fill` | take `N - c` units out of the container, as many as it actually holds |
| `c == N` | nobody | nothing moves |
| `c > N` | `ContainerTransfer` | the keeper logic leaves `N` behind and moves the surplus in |

Units and weight:

- A stack contributes `m_Units` units; a non-stack item contributes 1. `GearMath.UnitsOf` is the only counter.
- Per-unit weight is `GetItemWeightKG(false) / m_Units`. Ordering and merge checks use per-unit weight; capacity checks use the moving part's weight.
- A partial take of `k` units out of `n` is expected to change weight by `stackWeight * k / n`. Any move whose weight delta is outside 0.5×–2× of that is rolled back.

Counting scope is deliberately asymmetric:

- `TargetCount.CountInInventory` counts **every** backpack instance, including worn, held and satchel items. The skip settings decide what may move; they must not decide how many units the player owns, or a held tool reads as zero and a spare copy is pulled out of the container.
- `PickKeepers` only considers `stock`, which is filtered by those same settings. A protected instance therefore counts toward N but cannot be moved, so the backpack can end up holding **more** than N. Moving less is preferable to undressing the player.

## 6. Move actions

### 6.1 Move-in (backpack → container)

| Action | When | Key point |
| --- | --- | --- |
| `MoveOne` | the whole instance moves | `RemoveGear` then `AddGear`; if the container did not take it, it is returned to the backpack |
| `MergeInto` | a mergeable stack exists in the container and everything moves | only `m_Units` changes, then the source is destroyed; the container weight must rise by about one item |
| `MergeUnits` | a mergeable stack exists and part of the instance stays | both sides' `m_Units` change; container gain and backpack loss both checked |
| `SplitAndMove` | no stack to merge with, part stays | `InstantiateStackable(k)`, clear the backpack flags, `AddGear`; failure destroys the clone and restores the source |

Keeping rule: `PickKeepers` ranks candidates per kind — lowest condition first (good pieces get stored), then the lighter per-unit item (the 1 kg meat goes into the container, the 0.5 kg piece stays), then instance id for a stable result. Turning `KeepBestInContainer` off reverses the condition order.

Capacity gate: when the moving part does not fit, `FitUnits(remaining, unitWeight, units)` returns how many units do. Only a zero result skips the stack, so a 50 kg pile still yields the dozens of units that fit into 40 kg of room. The loop breaks only when the remaining capacity is at or below the tolerance, which reads as "keep going until not even one unit fits".

### 6.2 Take-out (container → backpack)

| Action | When | Key point |
| --- | --- | --- |
| `MoveWhole` | the whole stack is taken | `container.RemoveGear(gi, true)`; verify the landing (instance or weight), then `AddGear`, then put it back into the container |
| `ShiftUnits` | a compatible backpack stack exists | only `m_Units` on both sides; container loss and backpack gain both checked, both rolled back on doubt |
| `InstantiatePart` | no backpack stack to merge into | `InstantiateStackable(k)`, deduct the container stack, verify, restore on failure |

### 6.3 The weight-ledger gate (do not remove)

Editing `m_Units` is the cheapest way to move units and the easiest way to corrupt the encumbrance display. After every such edit both sides are checked against the expected delta:

- weight did not change → this item's weight ignores `m_Units`
- weight changed by far more than expected → units were already part of the weight and got counted twice

Either result means the item cannot be merged safely: units are restored and the caller falls back to a plain move (its own stack). The tolerance is `WeightMatches` in `GearMath` and it is shared by both directions.

### 6.4 Panel refresh

The mod changes data directly, so vanilla's own move handler never runs, and the panel keeps rendering the old rows. Three steps, each wrapped separately so one failure cannot undo finished moves:

| Step | Call | Why it is needed |
| --- | --- | --- |
| 1 | `Panel_Container.RefreshTables()` | rebuild the container and backpack tables immediately |
| 2 | `Panel_Container.ForceRefresh()` | sets the force-rebuild flag; the next frame recomputes the filter list and selection. Without it, moved rows can stay visible |
| 3 | `Panel_Inventory.RefreshTable()` | refresh the inventory panel's grid and weight when it is open |

### 6.5 Curing box (its own panel, its own storage)

`INTERACTIVE_CURINGBOX` is not a container. `CuringStorage` derives from `DecorationItem`, has no `Container` component, and opens `Panel_CuringStorage`; `Panel_Container` and `Panel_CuringStorage` are siblings under `Panel_AutoReferenced`, and `Panel_Container.ContainerMode` has only `Standard` and `Mission`. That is why the press did nothing: the container gate was false and returned silently.

| Aspect | Regular container | Curing box |
| --- | --- | --- |
| gate | `Panel_Container` | `Panel_CuringStorage` (routed in step 0) |
| storage list | `Container.m_Items` | `CuringStorage.CuringItems` |
| capacity | `m_Capacity` − `GetTotalWeightKG()` | `m_MaxStorage` − `TotalWeight` (meat only; salt is separate) |
| add | `Container.AddGear` | `CuringStorage.AddItem` |
| refresh | `RefreshTables` + `ForceRefresh` | `ForceRefreshTables` |

Rules that differ from the container path:

- **Same kind only.** The kinds moved in are exactly the ones already in the box. The game does not police what may be cured (`AddItem` accepts anything), so mirroring the contents keeps the mod inside what the player already stored instead of inventing an allow list.
- **The capacity gate is ours.** `CuringStorage.AddItem` validates nothing; the vanilla panel is what compares `GetItemWeightKG + TotalWeight` against `m_MaxStorage` in `MoveItem`. Calling `AddItem` directly can overfill the box, so every item is checked here first.
- **The running total is local.** `TotalWeight` is only recomputed in the box's own `Update`, so reading it after each add returns a stale value; the pass keeps its own `used` and adds each item's weight.
- **Vanilla call order.** `AddItem` first (it parents the object under the box), then `Inventory.RemoveGear(gameObject, true)`. Reversing them leaves the object detached from both sides when the second step fails.
- **Whole objects only.** The box records one curing entry per object, so a stack would be stored and cured as a single unit. Stacks are skipped with a log line.
- **No take-out.** The vanilla take-out path is `PlayerManager.AddItemToPlayerInventory` → `CuringStorage.RemoveItem` → `GearManager.DestroyGearObject`, and it rebuilds the object; a `ID*N` rule therefore only trims the surplus into the box, never tops the backpack up from it.

## 7. Ignore-list parsing
```
#<label>/<label>          section marker: the whole comment must be the marker
GEAR_Revolver             bare ID: never move in
GEAR_HeavyBandage*4       ID with a target count: both directions
```

- **An entry with `*N` is bidirectional** (`IgnoreMode.SetCount`); a bare entry inherits its section (`IgnoreAll` / `KeepOne`, one direction only). A number in the marker is only a default keep count and never makes an entry bidirectional.
- **Marker recognition** (`TryParseMarker`) accepts only a comment that consists of the marker. The body is split on `/` and its full-width variant, at most two parts; every part is squashed (whitespace removed, lower-cased) and must match a keyword **as a whole segment**. One unrecognised part and the line is an ordinary comment. This is what keeps the annotated example lines in the template from switching modes — **do not replace it with a `contains` check**.
- Keywords are matched in both languages; numbers accept digits and the Chinese numerals one to ten, default 1. Entries before the first marker default to `IgnoreAll`, which keeps old lists working.
- **The separator is `*`**, and the full-width variant is accepted. `#` only starts a comment. A `#N` form is reported once (only when `#` is directly followed by digits, so ordinary trailing notes stay quiet) instead of silently degrading to "keep one, move out only".
- After `*` the digits must be a run of digits followed by end of line, whitespace or a separator; anything else drops the suffix and warns. Text after the digits is treated as a note.
- Multiple entries per line are allowed (comma, semicolon, tab); `//` and `#` both start comments, including at the end of a line.
- The `GEAR_` prefix is optional: a stripped alias is stored for every entry, but counters only count what the player wrote, and `targets` never receives an alias.
- **Hot reload** compares `File.GetLastWriteTimeUtc`; a deleted file clears the rules and warns once.

## 8. Settings

| Field | Default | Range | Effect and when it applies |
| --- | --- | --- | --- |
| `Key` | `R` | any `KeyCode` | the sort key; rebinding happens through the ModSettings control |
| `RequireCtrl` | off | — | requires Ctrl as well, for keyboards already crowded by other mods |
| `MergeStacks` | on | — | merge into an existing container stack; off drops every item as its own stack |
| `SkipEquipped` | on | — | skip worn clothing and the held item; off effectively forces unequipping |
| `SkipSatchel` | on | — | skip items inside the satchel |
| `UseCuringBox` | on | — | off makes the curing box inert; the press is still claimed so it cannot fall through to the container path |
| `UseIgnoreList` | on | — | off disables the list entirely, including the take-out direction |
| `KeepBestInContainer` | on | — | keep the lowest condition, store the good piece; off keeps the best instead |
| `HeavierFirstWhenEqualCondition` | on | — | heavier per-unit items move first and stay stored last |
| `Verbose` | off | — | per-item log lines |

The settings page is shown in Chinese; the labels live in `Settings.cs` and are quoted verbatim in the README.

## 9. Log dictionary

Every run prints a summary; per-item detail appears only with `Verbose`. The literal text is Chinese (the log is read by the author), so the table keys each line by what it reports.

| Line | Printed when | How to read it |
| --- | --- | --- |
| capacity line | after the top-up pass | capacity, used and remaining weight; the same line adds how much was taken back out of the container |
| target-count summary | `Fill` moved something or found a shortfall | units taken back and how many kinds; a shortfall means the container did not hold enough |
| per-kind take line (verbose) | one kind was topped up | `before → after (target)`, plus the units still missing |
| per-item skip line (verbose) | a stack could not take a single unit | the remaining capacity and the weight of one unit |
| partial-move line (verbose) | the stack had to be split by capacity | how many of the total units were moved |
| completion summary | end of every run | items moved and kg, units taken back, merges, ignore-list skips, kept items, partial moves, failures, remaining capacity |
| container did not take the item | `AddGear` refused or the weight did not move | the item was returned to the backpack; the vanilla capacity rule is stricter than ours |
| stack weight did not follow the units | merge or shift verification failed | the ledger ignores `m_Units`; the action was rolled back and the item moved as its own stack |
| backpack did not take the item | both landing checks failed after a take-out | the stack was put back into the container |
| keyboard unreadable | the legacy input API threw | the hotkey is disabled for the session instead of logging every frame |
| transfer failed | an exception escaped a run | logged for the first three failures and every 200th afterwards |
| curing box capacity line | a curing-box run starts | capacity, used and remaining weight of the meat side; salt is stated to be accounted separately |
| curing box full / empty | nothing can move | the box is at capacity, or holds no kind that the backpack also has |
| curing box summary | end of a curing-box run | items moved and kg, ignore-list skips, kept items, skipped stacks, items that did not fit, failures, remaining capacity |
| item not accepted by the box | neither the entry count nor the weight changed | the item was handed back to the backpack by `PlayerManager.AddItemToPlayerInventory` |

## 10. Design constraints

| # | Constraint | If violated | See |
| --- | --- | --- | --- |
| 1 | Act only while `Panel_Container` is open | pressing the key anywhere else would move items out of a closed container | §2 |
| 2 | Top-up runs before the capacity check | a full container would never refill the backpack, which is exactly when it is needed | §2 |
| 3 | Backpack snapshot is taken after the top-up | units just taken back read as missing and are taken again on the next press | §2 |
| 4 | `RemoveGear` before `AddGear` | `AddGear` can merge and destroy the object, so a second call hits a destroyed item | §6.1 |
| 5 | Partial moves edit `m_Units` only, with the weight check | a ledger that ignores units silently breaks the capacity math | §6.3 |
| 6 | Never continue after a failed step: roll back or return the item | items end up duplicated or lost, and the container weight lies | §6 |
| 7 | All item readings go through `GearMath` | two tolerances drift apart and "top up N" fights "keep N" — visible as counts flapping | §5 |
| 8 | Target counts include worn, held and satchel units | a held tool counts as zero and a spare is pulled out of the container | §5 |
| 9 | Refresh in three steps, each in its own try | moved rows stay on screen, or a refresh failure hides a finished move | §6.4 |
| 10 | Split with `InstantiateStackable`, never with a prefab load | loading a prefab blocks for the whole bundle and freezes the panel | §4.1 |
| 11 | Section markers match whole segments, never `contains` | template example lines switch sections and silently change what is moved | §7 |
| 12 | `targets` never stores the `GEAR_`-less alias | the same rule is applied twice per press | §7 |
| 13 | Only an entry's `*N` is bidirectional | a marker number would start pulling items out of containers the player never meant to touch | §7 |
| 14 | Summary logging by default, per-item only with `Verbose` | a full container produces hundreds of lines per press | §9 |
| 15 | Route `Panel_CuringStorage` before the container gate | the container gate is false while a curing box is open and the press would do nothing | §2, §6.5 |
| 16 | Check curing-box capacity here, never rely on `AddItem` | `AddItem` validates nothing and will overfill the box | §6.5 |
| 17 | Add first, then remove from the backpack (curing box) | the reverse order drops the object on the floor when the removal fails | §6.5 |
| 18 | Never take items out of a curing box | the vanilla path rebuilds the object and destroys the stored copy; an interrupted take-out loses the meat | §6.5 |

## 11. Comment migration map

Every narrative that used to live in a source comment and now lives here. Rows are added whenever a comment is trimmed, so nobody has to guess whether information was lost.

| Original location | Original content | Now |
| --- | --- | --- |
| `ContainerSorterMod.cs` class header | key flow, target counts, where the settings live | §2, §8 |
| `ContainerTransfer.cs` class header | core move-in narrative | §2 |
| `ContainerTransfer.cs` `Run` inline notes | "top-up before the capacity check", "snapshot after the top-up" | §2, §10 rows 2–3 |
| `ContainerTransfer.cs` `PickKeepers` | keep-N selection order and the setting that reverses it | §6.1 |
| `ContainerTransfer.cs` `OrderForTransfer` | three-level ordering (kept last, heavy first, kinds stable) | §6.1 |
| `ContainerTransfer.cs` `RefreshPanels` | why three steps and why `ForceRefresh` is mandatory | §6.4 |
| `ContainerTransfer.cs` `MergeInto` / `MergeUnits` | weight-ledger reasoning (no change vs double counting) | §6.3 |
| `ContainerTransfer.cs` `FitUnits` | floor plus multiplication re-check | §6.1 |
| `ContainerTransfer.cs` `MoveOne` step 2 | claimed `AddGear` merges stacks — it does not | §4.1 (claim corrected in the comment) |
| `TargetCount.cs` `Fill` parameters | shortfall semantics and the ordering rule | §2, §5 |
| `TargetCount.cs` `CountInInventory` | why worn/held/satchel units count toward N | §5 |
| `TargetCount.cs` `ShiftUnits` / `InstantiatePart` | rollback order and the landing checks | §6.2 |
| `IgnoreList.cs` class header | file format, section markers, hot reload | §7, §8 |
| `CuringBoxTransfer.cs` class header | why the box is not a container and which panel it uses | §6.5, §4.1 |
| `CuringBoxTransfer.cs` `CollectKinds` | why only kinds already inside are moved | §6.5 |
| `CuringBoxTransfer.cs` `MoveInto` | vanilla call order and the landing check | §6.5 |
| `IgnoreList.cs` `targets` field | why target rules are stored apart from the alias table | §7, §10 row 12 |
| `IgnoreList.cs` `TryParseMarker` / `SplitEntry` | whole-segment matching, `*` versus `#`, full-width variant | §7 |
| `IgnoreList.cs` `ExtractEntryText` | the `#N` warning and why it only fires on digits | §7 |
| comments on `m_StackConditionDifferenceConstraint` | the 5% default when vanilla leaves 0 | §4.1 |
| comments mentioning earlier behaviour ("previously", "before the switch") | behaviour history | [CHANGELOG.md](CHANGELOG.md) |

## 12. Maintenance conventions

- Comments answer "what does this do / why must it be this way / what breaks otherwise". They carry no version numbers, no log excerpts and no history — those go to this document and the CHANGELOG.
- Adding or changing a constraint means adding a row to §10 **and** leaving the matching `§10 row N` pointer in the comment.
- Trimming a comment means adding a row to §11. A missing row reads as lost information.
- A new setting touches `Settings.cs`, the §8 table and both language blocks of the README.
- A new or changed log line touches §9.
- Game member claims are re-checked against the dump before being written down; anything unverified is marked as such.
- Behaviour that the player can observe changes in the CHANGELOG and in both README language blocks in the same edit.
- Version numbers exist in exactly four places: `MelonInfo` in `ContainerSorterMod.cs`, this document's header, the CHANGELOG's first heading, and the README badge.
- Nothing is committed on the player's behalf; the working copy is left for the author to test and commit.

## 13. Build and deploy

```bash
dotnet build -c Release
cp bin/Release/net6.0/ContainerSorter.dll "<game folder>/Mods/"
```

- `net6.0`, `Nullable=enable`, `GenerateAssemblyInfo=false` (assembly metadata comes from `MelonInfo`).
- Game types live in the `Il2Cpp` namespace, imported globally with `<Using Include="Il2Cpp" />`.
- `STBlade.Modding.TLD.Il2CppAssemblies.Windows` 2.55.0 supplies MelonLoader, HarmonyX, UnityEngine and Assembly-CSharp transitively. Do **not** add a `HintPath` for MelonLoader, it duplicates the reference.
- `ModSettings.dll` is referenced from the live `Mods\` folder with `Private=false`; the game ships it at runtime.
- `TheLongDarkPath` in the csproj points at the install that is being built against; change it when switching copies.
- Deploy pitfall: the game keeps the dll open. Move the old file away before copying, and never leave `*.old.dll` inside `Mods\` — the loader will treat it as a second mod.

## 14. Verification status

| Area | State |
| --- | --- |
| Build | `dotnet build -c Release` — 0 warnings, 0 errors |
| Comments and docs | `check_docs.py --lang en` and `check_readme.py` — no findings |
| Game members | checked against the 2.55 dump; `AddGear` merge behaviour and `RemoveGear(toInventory)` landing are the two claims that cannot be read from IL2CPP, so both are verified at runtime instead |
| Offline logic | stub project with the repository sources linked (`IgnoreList.cs`, `GearMath.cs`, `TargetCount.cs`, `ContainerTransfer.cs`, `CuringBoxTransfer.cs`): 20 scenarios, 70 assertions |
| Live behaviour | transfer, merges, capacity splitting and panel refresh exercised in game by the author; the log is the source of truth |
| Curing box | contract read from the 2.55 dump and disassembly (`Panel_CuringStorage.MoveItem`, `CuringStorage.AddItem`); the move-in path is covered offline; live runs still pending |
| Not verified | behaviour under other mods that rewrite stack weights or container capacity; only the weight-ledger gate protects that path |
