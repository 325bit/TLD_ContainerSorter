# Changelog

## 1.11.0 — the curing box works too

Why: pressing the sort key with a curing box open did nothing at all. The box is not a `Container` and opens its own panel, so the container gate was false and returned without a word.

- Added a curing-box pass. It routes on `Panel_CuringStorage` before the container gate and moves backpack items of the kinds already inside the box into it, up to the box's meat capacity.
- Salt is not touched: the box keeps its own salt storage and its own salt weight, and the meat capacity is read on its own.
- The capacity check lives in this mod because `CuringStorage.AddItem` validates nothing — not the kind, not the weight. The vanilla panel is the only thing that used to enforce it.
- Items go in whole; a stack is skipped with a log line, since the box records one curing entry per object.
- The ignore list applies as before: Ignore All skips, keep counts stay in the backpack, and a `ID*N` target only trims the surplus — nothing is ever taken out of a box.
- New setting `UseCuringBox` (on by default) turns the whole path off without falling back to the container logic.
- Offline stub project extended with the curing-box scenario set.

## 1.10.0 — target counts work in both directions

Why: a keep count could only stop items from going into the container. What players actually wanted was a fixed number of arrows, ammo or bandages in the backpack, without doing the subtraction themselves.

- Added target counts: `ID*N` in the ignore list keeps the backpack at N units. A surplus moves into the container under the existing rule, a shortfall is taken back out of it (as much as the container holds).
- Only `ID*N` behaves this way. Bare entries and numbers inside a section marker stay one-way, as before.
- Partial takes merge into a matching backpack stack first (condition gap permitting) and split a new stack only when there is nothing to merge with. Every action checks that the container lost and the backpack gained roughly the weight of those units, and rolls back when it does not.
- Taking items out is not limited by the container's remaining capacity and runs before the move-in pass, which frees space for it.
- The completion summary gained "units taken back"; item readings (units, weight, condition, tolerance) were extracted into a shared module so both directions use one set of rules.
- Added an offline stub project covering parsing and the take-out logic; run it before touching either area.

Documentation, no behaviour change:

- README rewritten as a bilingual package (English block first, Chinese block second) with the settings table, ignore-list section and FAQ kept in sync between the two.
- `docs/DEVELOPMENT.md` restructured into the 14-section maintainer layout, now in English, including the design-constraint table and the comment migration map.
- All source comments translated to English and trimmed to conclusions; the narratives they carried moved into the development document.

## 1.9.0 — the count separator became `*`

Why: `#` was both the comment marker and the count separator, so `GEAR_X # 4 spare` could be read as either. One character cannot mean two things.

- The count separator is `*`; `#` is a comment marker again and stays out of count parsing.
- The full-width variant is recognised too, and a `*` that is not followed by a number drops the suffix with a warning.
- The retired `GEAR_X#4` form no longer works; the log points at `*` once per offending line.
- The generated list template is bilingual.

## 1.8.0 — keep N units, not just one instance

Why: "keep one" was expressed as one instance, which is wrong for stacks — one stack of 20 arrows is not one arrow.

- The section label became Keep At Least One; the older labels are still accepted.
- Entries accept `ID#N` to set the count, defaulting to 1.
- Keeping is now counted in units, so a kind that does not stack can also be limited to N pieces.

## 1.7.0 — split a stack instead of giving up on it

Why: a 100-pelt stack weighing 50 kg was skipped entirely in a container with 40 kg of room, although dozens of pelts would have fit.

- Oversized stacks are split by `remaining capacity / unit weight` and moved part by part, until not even one unit fits.
- The summary line reports how many stacks were moved only partially.

## 1.6.0 — heavier pieces first when condition ties

Why: with equal freshness, the 1 kg meat should be stored and the 0.5 kg piece kept, but the order was arbitrary.

- When conditions differ by 0.1% or less, the lighter per-unit item stays in the backpack and the heavier one is stored.
- Within a kind, heavier per-unit items now move first, so a nearly full container takes them.
- Added the "heavier first when condition ties" setting.

## 1.5.0 — refresh the panels after moving

Why: items disappeared from the data but stayed visible in the list until the panel was reopened.

- Refresh after every run: rebuild the tables, request a forced rebuild for the next frame, and refresh the inventory panel as well.
- Fixed rows of already moved items lingering in the list.

## 1.4.0 — keep the worst, store the best

Why: keeping an arbitrary instance of a kind meant the good copy could end up in the container and the worn one in the backpack.

- With different conditions, the lowest condition stays in the backpack and the good piece is stored.
- Added the "keep the best in the container" setting.

## 1.3.0 — sections in the ignore list

Why: one flat list could not express "never move this" and "keep one of these" at the same time.

- The list supports sections: Ignore All and Keep One.
- Keeping also works for items that do not stack: with several of a kind, one stays and the rest move in.
- Section labels are bilingual.

## 1.2.0 — an ignore list on disk

Why: some kinds should never be sorted automatically, and hard-coding them means a rebuild for every preference change.

- Added `Mods/ContainerSorterIgnore.txt`, generated with a template on first run.
- Comments, several entries per line and an optional `GEAR_` prefix are supported; the file reloads when its timestamp changes, so no restart is needed.

## 1.1.0 — a real key binding, and stack merging

Why: a fixed key collides with other mods, and moving 20 firewood next to an existing log produced two stacks instead of 21.

- The key is a genuine binding (ModSettings rebind control) and can be changed freely.
- Stackable items merge into an existing container stack instead of landing as a new one.
- Added the "merge stackable items" setting.

## 1.0.0 — first release

- Sort on a key while a container panel is open: kinds the container already holds move in, limited by the remaining capacity.
- Skip toggles for worn, held, satchel and hidden-supply items, plus a verbose log.
