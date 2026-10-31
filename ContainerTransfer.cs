using System;
using System.Collections.Generic;
using Il2CppTLD.IntBackedUnit;
using MelonLoader;

namespace ContainerSorter;

/// <summary>
/// Move-in pass: while a container panel is open, every backpack item whose kind the container already holds is moved in
/// until the remaining capacity cannot take another unit.
/// Kinds listed as <c>ID*count</c> are target counts: <see cref="TargetCount"/> takes the shortfall back out first, then
/// this class moves the surplus in following the same "keep N" rule.
/// Pipeline and constraints: docs/DEVELOPMENT.md §2 and §10.
/// </summary>
internal static class ContainerTransfer
{
	/// <summary>kg comparison tolerance, so floating-point drift does not lock out an item that fits exactly.</summary>
	private const float Epsilon = 0.0005f;

	internal static void Run(MelonLogger.Instance log, Settings cfg)
	{
		// The curing box is not a Container and opens Panel_CuringStorage, so it gets its own pass first.
		// A true result means this key press was handled there and this method must not run.
		if (CuringBoxTransfer.Run(log, cfg))
		{
			return;
		}

		Panel_Container panel;
		try
		{
			panel = InterfaceManager.GetPanel<Panel_Container>();
		}
		catch (Exception e)
		{
			log.Warning("取 Panel_Container 失败：" + e.Message);
			return;
		}

		// No container panel open: do nothing and stay silent, otherwise every key press writes a log line
		if (panel == null || !SafeIsEnabled(panel))
		{
			return;
		}

		Container container = panel.m_Container;
		if (container == null)
		{
			log.Warning("容器面板已打开，但 m_Container 为空，跳过。");
			return;
		}

		Inventory inv = GameManager.GetInventoryComponent();
		if (inv == null)
		{
			log.Warning("拿不到 Inventory 组件，跳过。");
			return;
		}

		// The list reloads itself when its timestamp changes: save the file, press the key again
		if (cfg.UseIgnoreList)
		{
			IgnoreList.Reload(log);
		}

		// Target counts first: take the missing units back out of the container.
		// This runs before the capacity check because taking items out frees space instead of using it.
		int pulledUnits = 0;
		int pulledKinds = 0;
		if (cfg.UseIgnoreList)
		{
			TargetCount.Fill(container, inv, log, cfg, out pulledUnits, out pulledKinds);
		}

		float capacity = GearMath.ToKilograms(container.m_Capacity);
		float used = GearMath.ToKilograms(container.GetTotalWeightKG());
		float remaining = capacity - used;

		log.Msg($"[{ContainerName(container)}] 容量 {capacity:0.##}kg，已用 {used:0.##}kg，剩余 {remaining:0.##}kg"
			+ (pulledUnits > 0 ? $"，已从容器取回 {pulledUnits} 份（{pulledKinds} 种）补足目标数量" : string.Empty));

		if (capacity <= 0f)
		{
			log.Warning("这个容器容量为 0，原版也不允许存放，跳过搬入。");

			if (pulledUnits > 0)
			{
				RefreshPanels(panel, log);
			}

			return;
		}

		if (remaining <= Epsilon)
		{
			log.Msg("容器已经装满，没有可迁移的空间。");

			if (pulledUnits > 0)
			{
				RefreshPanels(panel, log);
			}

			return;
		}

		HashSet<string> containerKinds = CollectContainerKinds(container);
		if (containerKinds.Count == 0)
		{
			log.Msg("容器是空的，没有可以匹配的物品。");

			if (pulledUnits > 0)
			{
				RefreshPanels(panel, log); // 取回把容器掏空了，界面还停着旧条目
			}

			return;
		}

		// Snapshot the backpack after the top-up, so the units just taken back count against the keep rule instead of being taken twice
		List<GearItem> stock = CollectInventoryItems(inv, cfg);

		// Pick which instances of a "keep N" kind stay in the backpack before anything moves
		Dictionary<string, List<KeeperPick>> keepers = cfg.UseIgnoreList
			? PickKeepers(stock, containerKinds, cfg)
			: new Dictionary<string, List<KeeperPick>>(StringComparer.OrdinalIgnoreCase);

		// Move order: fully moved items before the ones staying behind, heavier units first within a kind, so a nearly
		// full container takes the heavy pieces. Kinds keep their backpack order.
		if (cfg.HeavierFirstWhenEqualCondition)
		{
			stock = OrderForTransfer(stock, keepers);
		}

		int moved = 0;
		int merged = 0;
		int tooHeavy = 0;
		int failed = 0;
		int ignored = 0;
		int kept = 0;
		int partialMoves = 0;
		float movedKg = 0f;

		foreach (GearItem gi in stock)
		{
			string? name = GearMath.SafeName(gi);
			if (name == null)
			{
				continue;
			}

			string kind = GearMath.NormalizeName(name);
			if (!containerKinds.Contains(kind))
			{
				continue; // The container holds no item of this kind
			}

			IgnoreRule rule = cfg.UseIgnoreList ? IgnoreList.Lookup(kind) : IgnoreRule.None;

			if (rule.Mode == IgnoreMode.IgnoreAll)
			{
				ignored++;
				if (cfg.Verbose)
				{
					log.Msg($"  × {name} 在忽略清单（全部无视）里，跳过");
				}

				continue;
			}

			int units = 1;
			bool stackable = false;
			try
			{
				StackableItem? st = gi.m_StackableItem;
				if (st != null)
				{
					units = st.m_Units;
					stackable = units > 0;
				}
			}
			catch
			{
				continue;
			}

			if (stackable && units <= 0)
			{
				continue;
			}

			// A positive keep means part of this instance stays in the backpack
			int keep = 0;
			if (IsKeepRule(rule.Mode))
			{
				bool isKeeper = TryGetKeeper(keepers, kind, gi, out int keeperUnits);

				if (!isKeeper)
				{
					keep = 0; // Nothing of this kind has to stay, so the whole instance moves
				}
				else if (!stackable)
				{
					keep = 1; // Not a stack: this very instance is the one that stays
				}
				else
				{
					keep = Math.Min(keeperUnits, units);
				}

				if (keep >= units)
				{
					kept++;
					if (cfg.Verbose)
					{
						float cond = GearMath.Condition(gi);
						float unit = GearMath.UnitKilograms(gi);
						log.Msg($"  = {name} 按「保留」留在背包（{units} 个"
							+ (cond >= 0f ? $"，耐久 {cond * 100f:0}%" : string.Empty)
							+ (unit > 0f ? $"，单份 {unit:0.##}kg" : string.Empty) + "）");
					}

					continue; // Nothing left to move
				}
			}

			int moveUnits = units - keep;
			float itemKg = GearMath.ItemKilograms(gi);
			float unitKg = stackable && units > 0 ? itemKg / units : itemKg;
			float moveKg = keep > 0 ? unitKg * moveUnits : itemKg;

			float left = capacity - GearMath.ToKilograms(container.GetTotalWeightKG());

			if (moveKg > left + Epsilon)
			{
				// The whole stack does not fit: move the part that does and leave the rest behind.
				// Only "not even one unit fits" is a real skip — 100 pelts weighing 50kg must still give up the
				// dozens that fit into 40kg of room.
				int fitUnits = stackable ? FitUnits(left, unitKg, moveUnits) : 0;

				if (fitUnits <= 0)
				{
					tooHeavy++;
					if (cfg.Verbose)
					{
						log.Msg($"  跳过 {name}（{moveKg:0.###}kg > 剩余 {left:0.###}kg，连一份都放不下）");
					}

					continue;
				}

				if (cfg.Verbose)
				{
					log.Msg($"  剩余 {left:0.###}kg 只够 {fitUnits}/{units} 份（共 {moveKg:0.###}kg），先搬这些");
				}

				keep = units - fitUnits;   // Whatever does not fit stays behind
				moveUnits = fitUnits;
				moveKg = unitKg * moveUnits;
				partialMoves++;
			}

			bool ok;
			bool mergedNow = false;

			if (keep > 0)
			{
				// Partial move: merge into an existing container stack when possible (the source stays in the backpack and
				// only its unit count changes), otherwise split off a new instance.
				GearItem? mergeTarget = cfg.MergeStacks ? FindMergeTarget(container, gi, kind) : null;
				if (mergeTarget != null)
				{
					mergedNow = MergeUnits(gi, mergeTarget, moveUnits, keep, log, cfg.Verbose);
				}

				ok = mergedNow || SplitAndMove(gi, container, moveUnits, keep, log, cfg.Verbose);
			}
			else
			{
				GearItem? mergeTarget = cfg.MergeStacks ? FindMergeTarget(container, gi, kind) : null;
				if (mergeTarget != null)
				{
					mergedNow = MergeInto(gi, mergeTarget, inv, log, cfg.Verbose);
				}

				// Nothing to merge with, or merging failed (the weight ledger ignores units): fall back to a plain move
				ok = mergedNow || MoveOne(gi, container, inv, log, itemKg, cfg.Verbose);
			}

			if (ok)
			{
				moved++;
				if (mergedNow)
				{
					merged++;
				}

				movedKg += moveKg;
			}
			else
			{
				failed++;
			}

			if (capacity - GearMath.ToKilograms(container.GetTotalWeightKG()) <= Epsilon)
			{
				break; // Full
			}
		}

		RefreshPanels(panel, log);

		float nowLeft = capacity - GearMath.ToKilograms(container.GetTotalWeightKG());
		log.Msg($"整理完成：放入 {moved} 件（{movedKg:0.##}kg）"
			+ (pulledUnits > 0 ? $"，从容器取回 {pulledUnits} 份（{pulledKinds} 种）" : string.Empty)
			+ (merged > 0 ? $"，其中 {merged} 件并进已有堆叠" : string.Empty)
			+ (ignored > 0 ? $"，{ignored} 件按忽略清单跳过" : string.Empty)
			+ (kept > 0 ? $"，{kept} 件按「保留」留在背包" : string.Empty)
			+ (partialMoves > 0 ? $"，{partialMoves} 堆因剩余容量只搬了一部分" : string.Empty)
			+ (tooHeavy > 0 ? $"，{tooHeavy} 件放不下" : string.Empty)
			+ (failed > 0 ? $"，{failed} 件失败" : string.Empty)
			+ $"，容器剩余 {nowLeft:0.##}kg");
	}

	/// <summary>
	/// Refreshes the panels after moving: rebuild the tables, request a forced refresh for the next frame, then refresh the
	/// inventory panel when it is open. This mod edits data directly, so vanilla never runs its own move handler and the
	/// filter lists keep showing moved items. Each step is guarded on its own, so one failure cannot undo the moves.
	/// Details: docs/DEVELOPMENT.md §6.4.
	/// </summary>
	private static void RefreshPanels(Panel_Container panel, MelonLogger.Instance log)
	{
		try
		{
			panel.RefreshTables();
		}
		catch (Exception e)
		{
			log.Warning("刷新容器面板表格失败：" + e.Message);
		}

		try
		{
			panel.ForceRefresh();
		}
		catch (Exception e)
		{
			log.Warning("请求物品栏整表重建失败：" + e.Message);
		}

		try
		{
			if (InterfaceManager.TryGetPanel<Panel_Inventory>(out Panel_Inventory invPanel)
				&& invPanel != null && invPanel.IsEnabled())
			{
				invPanel.RefreshTable();
			}
		}
		catch (Exception e)
		{
			log.Warning("刷新背包面板失败：" + e.Message);
		}
	}

	/// <summary>
	/// True for rules that keep N units in the backpack. <see cref="IgnoreMode.KeepOne"/> and <see cref="IgnoreMode.SetCount"/>
	/// are identical on the move-in side; only <see cref="IgnoreMode.SetCount"/> also has its shortfall filled by
	/// <see cref="TargetCount"/>.
	/// </summary>
	internal static bool IsKeepRule(IgnoreMode mode)
	{
		return mode == IgnoreMode.KeepOne || mode == IgnoreMode.SetCount;
	}

	/// <summary>
	/// Chooses the instances that stay in the backpack for each "keep N" kind that the container also holds.
	/// N comes from the list (<c>ID*N</c>, default 1) and stacks count by unit until N is reached.
	/// Lowest condition stays first, then the lighter unit, so good and heavy pieces end up stored.
	/// Turning the "keep the best in the container" setting off reverts to keeping the best piece instead.
	/// </summary>
	internal static Dictionary<string, List<KeeperPick>> PickKeepers(List<GearItem> stock, HashSet<string> containerKinds, Settings cfg)
	{
		var result = new Dictionary<string, List<KeeperPick>>(StringComparer.OrdinalIgnoreCase);
		var candidates = new Dictionary<string, List<GearItem>>(StringComparer.OrdinalIgnoreCase);
		var order = new List<string>();

		foreach (GearItem gi in stock)
		{
			string? name = GearMath.SafeName(gi);
			if (name == null)
			{
				continue;
			}

			string kind = GearMath.NormalizeName(name);
			if (!containerKinds.Contains(kind) || !IsKeepRule(IgnoreList.Lookup(kind).Mode))
			{
				continue;
			}

			if (!candidates.TryGetValue(kind, out List<GearItem>? list))
			{
				list = new List<GearItem>();
				candidates[kind] = list;
				order.Add(kind);
			}

			list.Add(gi);
		}

		foreach (string kind in order)
		{
			List<GearItem> list = candidates[kind];
			int need = Math.Max(1, IgnoreList.Lookup(kind).KeepCount);

			// Earlier means more likely to stay: low condition, then light unit, then instance id for a stable result
			list.Sort((a, b) => CompareKeepPriority(a, b, cfg));

			var picks = new List<KeeperPick>();
			int remaining = need;

			foreach (GearItem gi in list)
			{
				if (remaining <= 0)
				{
					break;
				}

				int units = GearMath.UnitsOf(gi);
				int take = Math.Min(units, remaining);
				picks.Add(new KeeperPick(gi, take));
				remaining -= take;
			}

			if (picks.Count > 0)
			{
				result[kind] = picks;
			}
		}

		return result;
	}

	/// <summary>An instance that stays in the backpack and how many of its units stay.</summary>
	internal readonly struct KeeperPick
	{
		internal KeeperPick(GearItem item, int keepUnits)
		{
			Item = item;
			KeepUnits = keepUnits;
		}

		internal GearItem Item { get; }

		internal int KeepUnits { get; }
	}

	internal static bool TryGetKeeper(Dictionary<string, List<KeeperPick>> keepers, string kind, GearItem gi, out int keepUnits)
	{
		keepUnits = 0;

		if (!keepers.TryGetValue(kind, out List<KeeperPick>? picks) || picks == null)
		{
			return false;
		}

		for (int i = 0; i < picks.Count; i++)
		{
			if (IsSameInstance(gi, picks[i].Item))
			{
				keepUnits = picks[i].KeepUnits;
				return true;
			}
		}

		return false;
	}

	/// <summary>Sort helper: a negative result means <c>a</c> should stay in the backpack.</summary>
	private static int CompareKeepPriority(GearItem a, GearItem b, Settings cfg)
	{
		float condA = GearMath.Condition(a);
		float condB = GearMath.Condition(b);

		// No condition to compare (items without durability): fall through to weight
		if (condA >= 0f && condB >= 0f && Math.Abs(condA - condB) > 0.001f)
		{
			return cfg.KeepBestInContainer
				? condA.CompareTo(condB)   // keep the lowest condition
				: condB.CompareTo(condA);  // keep the highest condition
		}

		float kgA = GearMath.UnitKilograms(a);
		float kgB = GearMath.UnitKilograms(b);
		if (cfg.HeavierFirstWhenEqualCondition && kgA > 0f && kgB > 0f && Math.Abs(kgA - kgB) > 0.0001f)
		{
			return kgA.CompareTo(kgB); // keep the lighter unit
		}

		return GearMath.InstanceId(a).CompareTo(GearMath.InstanceId(b));
	}

	private static bool IsSameInstance(GearItem a, GearItem b)
	{
		try
		{
			return a != null && b != null && a.m_InstanceID == b.m_InstanceID;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Stable reorder inside each kind: fully moved items first, then heavier units, kinds keep their backpack order.
	/// Without it a kept item can take the space a moved item would have used.
	/// </summary>
	internal static List<GearItem> OrderForTransfer(List<GearItem> stock, Dictionary<string, List<KeeperPick>> keepers)
	{
		var groupRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		var keyed = new List<SortEntry>(stock.Count);

		foreach (GearItem gi in stock)
		{
			string? name = GearMath.SafeName(gi);
			string kind = name == null ? "\u0000invalid" : GearMath.NormalizeName(name);
			if (!groupRank.TryGetValue(kind, out int group))
			{
				group = groupRank.Count;
				groupRank[kind] = group;
			}

			keyed.Add(new SortEntry(gi, group, IsFullyKept(gi, kind, keepers) ? 1 : 0, GearMath.UnitKilograms(gi)));
		}

		keyed.Sort((a, b) =>
		{
			int c = a.Group.CompareTo(b.Group);
			if (c != 0)
			{
				return c;
			}

			c = a.Kept.CompareTo(b.Kept);
			if (c != 0)
			{
				return c;
			}

			return b.UnitKg.CompareTo(a.UnitKg); // Heaviest first
		});

		var ordered = new List<GearItem>(keyed.Count);
		foreach (SortEntry e in keyed)
		{
			ordered.Add(e.Item);
		}

		return ordered;
	}

	private readonly struct SortEntry
	{
		internal SortEntry(GearItem item, int group, int kept, float unitKg)
		{
			Item = item;
			Group = group;
			Kept = kept;
			UnitKg = unitKg;
		}

		internal GearItem Item { get; }
		internal int Group { get; }
		internal int Kept { get; }
		internal float UnitKg { get; }
	}

	/// <summary>True when the whole instance stays behind; only those have to be sorted last.</summary>
	private static bool IsFullyKept(GearItem gi, string kind, Dictionary<string, List<KeeperPick>> keepers)
	{
		return TryGetKeeper(keepers, kind, gi, out int keepUnits) && keepUnits >= GearMath.UnitsOf(gi);
	}

	// ---------------------------------------------------------------- Collection

	/// <summary>Kinds already in the container, compared by prefab name with the (Clone) suffix removed.</summary>
	private static HashSet<string> CollectContainerKinds(Container container)
	{
		var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var items = container.m_Items;
		if (items == null)
		{
			return kinds;
		}

		for (int i = 0; i < items.Count; i++)
		{
			try
			{
				GearItem gi = items[i].m_GearItem;
				string? name = GearMath.SafeName(gi);
				if (name != null)
				{
					kinds.Add(GearMath.NormalizeName(name));
				}
			}
			catch
			{
				// One unreadable entry must not abort the scan
			}
		}

		return kinds;
	}

	/// <summary>Backpack snapshot, taken before moving so the collection is not modified while it is walked.</summary>
	internal static List<GearItem> CollectInventoryItems(Inventory inv, Settings cfg)
	{
		var result = new List<GearItem>();

		var items = inv.m_Items;
		if (items == null)
		{
			return result;
		}

		int handsId = -1;
		if (cfg.SkipEquipped)
		{
			try
			{
				GearItem inHands = GameManager.GetPlayerManagerComponent().m_ItemInHands;
				if (inHands != null)
				{
					handsId = inHands.m_InstanceID;
				}
			}
			catch
			{
				// No PlayerManager at the main menu or during loading
			}
		}

		for (int i = 0; i < items.Count; i++)
		{
			try
			{
				GearItem gi = items[i].m_GearItem;
				if (gi == null || gi.m_IsHidden)
				{
					continue;
				}

				// Water and fuel supplies are internal stand-ins and must not be moved around
				if (gi.m_WaterSupply != null)
				{
					continue;
				}

				if (cfg.SkipSatchel && gi.m_IsInSatchel)
				{
					continue;
				}

				if (cfg.SkipEquipped)
				{
					if (gi.m_InstanceID == handsId)
					{
						continue;
					}

					ClothingItem clothing = gi.m_ClothingItem;
					if (clothing != null && clothing.IsWearing())
					{
						continue;
					}
				}

				StackableItem stack = gi.m_StackableItem;
				if (stack != null && stack.m_Units <= 0)
				{
					continue;
				}

				result.Add(gi);
			}
			catch
			{
				// Skip the instance, keep the rest
			}
		}

		return result;
	}

	// ---------------------------------------------------------------- Move-in actions

	private static bool MoveOne(GearItem gi, Container container, Inventory inv, MelonLogger.Instance log, float itemKg, bool verbose)
	{
		string name = GearMath.SafeName(gi) ?? "(未知物品)";

		int instanceId;
		float before;
		try
		{
			instanceId = gi.m_InstanceID;
			before = GearMath.ToKilograms(container.GetTotalWeightKG());
		}
		catch (Exception e)
		{
			log.Warning($"读物品状态失败（{name}）：{e.Message}");
			return false;
		}

		// 1) Detach from the backpack (toContainer = true: keep the object alive for the container)
		try
		{
			inv.RemoveGear(gi.gameObject, true);
		}
		catch (Exception e)
		{
			log.Error($"从背包移出失败（{name}）：{e.Message}");
			return false;
		}

		// 2) Hand it to the container; AddGear does not merge stacks, that is FindMergeTarget's job
		try
		{
			container.AddGear(gi);
		}
		catch (Exception e)
		{
			log.Error($"放入容器失败（{name}），尝试退回背包：{e.Message}");
			ReturnToInventory(gi, inv, log);
			return false;
		}

		// 3) Verify: the instance shows up in the container list, or the container weight rose by one item
		float after;
		try
		{
			after = GearMath.ToKilograms(container.GetTotalWeightKG());
		}
		catch
		{
			after = before + itemKg;
		}

		bool inContainer = ContainerHasInstance(container, instanceId);
		bool weightGrew = after >= before + itemKg - Epsilon;

		if (!inContainer && !weightGrew)
		{
			log.Warning($"容器没有接收 {name}（可能容量判定更严），已退回背包。");
			ReturnToInventory(gi, inv, log);
			return false;
		}

		// Safety net: a stale in-inventory flag would leave a copy on both sides
		try
		{
			if (gi != null && gi.m_InPlayerInventory)
			{
				log.Warning($"{name} 仍在背包标记里，补一次移出。");
				inv.RemoveGear(gi.gameObject, true);
			}
		}
		catch
		{
			// Expected when the object was merged into a container stack and destroyed
		}

		if (verbose)
		{
			log.Msg($"  + {name}  {itemKg:0.###}kg  →  容器 {after:0.##}kg");
		}

		return true;
	}

	// ---------------------------------------------------------------- Stack merging

	/// <summary>
	/// Finds a container stack the source can join, or null when the source has to land as a stack of its own.
	/// Same kind, both real stacks, no liquid, and a condition gap inside the vanilla limit.
	/// </summary>
	private static GearItem? FindMergeTarget(Container container, GearItem source, string kind)
	{
		try
		{
			StackableItem srcStack = source.m_StackableItem;
			if (srcStack == null || srcStack.m_Units <= 0 || source.m_LiquidItem != null)
			{
				return null; // Not a stack; liquids are accounted by volume and stay out of this
			}

			float maxDiff = srcStack.m_StackConditionDifferenceConstraint;
			if (maxDiff <= 0f)
			{
				maxDiff = 5f; // 原版没填时给 5% 的宽容度，别把新鲜食物和快烂的并一堆
			}

			float condition = source.GetNormalizedCondition();
			int sourceId = source.m_InstanceID;

			var items = container.m_Items;
			if (items == null)
			{
				return null;
			}

			GearItem? best = null;
			float bestDiff = float.MaxValue;

			for (int i = 0; i < items.Count; i++)
			{
				GearItem target = items[i].m_GearItem;
				if (target == null || target.m_InstanceID == sourceId || target.m_LiquidItem != null)
				{
					continue;
				}

				StackableItem tStack = target.m_StackableItem;
				if (tStack == null || tStack.m_Units <= 0)
				{
					continue;
				}

				string? targetName = GearMath.SafeName(target);
				if (targetName == null || !string.Equals(GearMath.NormalizeName(targetName), kind, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				float diff = Math.Abs(target.GetNormalizedCondition() - condition) * 100f;
				if (diff > maxDiff)
				{
					continue;
				}

				if (diff < bestDiff)
				{
					best = target;
					bestDiff = diff;
				}
			}

			return best;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Merges the source stack into an existing container stack and destroys the source. It detaches from the backpack
	/// first, the same way a plain move does, so the container never ends up with two stacks of one kind.
	/// </summary>
	private static bool MergeInto(GearItem source, GearItem target, Inventory inv, MelonLogger.Instance log, bool verbose)
	{
		string name = GearMath.SafeName(source) ?? "(未知物品)";

		int units;
		int targetUnits;
		try
		{
			units = source.m_StackableItem.m_Units;
			targetUnits = target.m_StackableItem.m_Units;
		}
		catch (Exception e)
		{
			log.Warning($"读取堆叠数量失败（{name}）：{e.Message}");
			return false;
		}

		if (units <= 0)
		{
			return false;
		}

		float weightBefore;
		try
		{
			weightBefore = GearMath.ToKilograms(target.GetItemWeightKG(false));
		}
		catch
		{
			weightBefore = -1f;
		}

		float expected;
		try
		{
			expected = GearMath.ToKilograms(source.GetItemWeightKG(false));
		}
		catch
		{
			expected = 0f;
		}

		try
		{
			inv.RemoveGear(source.gameObject, true);
		}
		catch (Exception e)
		{
			log.Error($"从背包移出失败（{name}）：{e.Message}");
			return false;
		}

		bool increased;
		float weightAfter;
		try
		{
			target.m_StackableItem.m_Units = targetUnits + units;
			increased = target.m_StackableItem.m_Units == targetUnits + units;
			weightAfter = GearMath.ToKilograms(target.GetItemWeightKG(false));
		}
		catch (Exception e)
		{
			log.Error($"并进容器堆叠失败（{name}）：{e.Message}");
			increased = false;
			weightAfter = weightBefore;
		}

		// Units went up, so weight must rise by about one item: no change means the ledger ignores m_Units, a much larger
		// jump means units were already counted. Both cases roll back and move the item as its own stack.
		bool weightTracks;
		if (weightBefore < 0f || expected <= 0.0001f)
		{
			weightTracks = true; // 没抓到基准重量 / 物品本身几乎没重量，不做这项校验
		}
		else
		{
			float delta = weightAfter - weightBefore;
			weightTracks = delta > expected * 0.5f && delta < expected * 2f;
		}

		if (!increased || !weightTracks)
		{
			if (increased)
			{
				try
				{
					target.m_StackableItem.m_Units = targetUnits; // Restore the unit count
				}
				catch
				{
					// Best effort
				}

				log.Warning($"{name} 的堆叠重量和数量对不上（应该 +{expected:0.###}kg，实际 +{weightAfter - weightBefore:0.###}kg），已回滚，改为单独放一堆。");
			}

			ReturnToInventory(source, inv, log);
			return false;
		}

		// The units now live in the target, so the emptied source can go
		try
		{
			UnityEngine.Object.Destroy(source.gameObject);
		}
		catch (Exception e)
		{
			log.Warning($"销毁已合并的空壳失败（{name}）：{e.Message}");
		}

		if (verbose)
		{
			log.Msg($"  ⇄ {name} ×{units} 并进容器已有堆叠（{targetUnits} → {targetUnits + units}）");
		}

		return true;
	}

	// ---------------------------------------------------------------- Partial move with a keep count

	/// <summary>
	/// Moves <c>moveUnits</c> units into an existing container stack while the source stays in the backpack at
	/// <c>keepUnits</c>. Only <c>m_Units</c> changes on both sides and the backpack API is never touched, which is the
	/// safest path. Any failed check rolls both sides back and returns false so the caller can try another route.
	/// </summary>
	private static bool MergeUnits(GearItem source, GearItem target, int moveUnits, int keepUnits, MelonLogger.Instance log, bool verbose)
	{
		string name = GearMath.SafeName(source) ?? "(未知物品)";

		int sourceUnits;
		int targetUnits;
		try
		{
			sourceUnits = source.m_StackableItem.m_Units;
			targetUnits = target.m_StackableItem.m_Units;
		}
		catch (Exception e)
		{
			log.Warning($"读取堆叠数量失败（{name}）：{e.Message}");
			return false;
		}

		if (moveUnits <= 0 || keepUnits <= 0 || sourceUnits != moveUnits + keepUnits || targetUnits <= 0)
		{
			return false;
		}

		float targetBefore = GearMath.SafeWeight(target);
		float sourceBefore = GearMath.SafeWeight(source);
		float expected = sourceBefore > 0f ? sourceBefore * moveUnits / sourceUnits : 0f;

		bool applied;
		try
		{
			target.m_StackableItem.m_Units = targetUnits + moveUnits;
			source.m_StackableItem.m_Units = keepUnits;

			applied = target.m_StackableItem.m_Units == targetUnits + moveUnits
				&& source.m_StackableItem.m_Units == keepUnits;
		}
		catch (Exception e)
		{
			log.Error($"并进堆叠失败（{name}）：{e.Message}");
			applied = false;
		}

		if (!applied)
		{
			GearMath.RestoreUnits(source, sourceUnits);
			GearMath.RestoreUnits(target, targetUnits);
			return false;
		}

		float targetAfter = GearMath.SafeWeight(target);
		float sourceAfter = GearMath.SafeWeight(source);

		// The container must gain and the backpack lose roughly the weight of those units
		bool grew = GearMath.WeightMatches(targetBefore, targetAfter, expected);
		bool shrank = GearMath.WeightMatches(sourceBefore, sourceAfter, -expected);

		if (!grew || !shrank)
		{
			GearMath.RestoreUnits(source, sourceUnits);
			GearMath.RestoreUnits(target, targetUnits);
			log.Warning($"{name} 的堆叠重量没有随数量变化（容器 {targetAfter - targetBefore:+0.###;-0.###;0}kg / 背包 {sourceAfter - sourceBefore:+0.###;-0.###;0}kg，期望 ±{expected:0.###}kg），已回滚。");
			return false;
		}

		if (verbose)
		{
			log.Msg($"  ⇄ {name} {moveUnits}/{sourceUnits} 并进容器已有堆叠（{targetUnits} → {targetUnits + moveUnits}），背包留 {keepUnits}");
		}

		return true;
	}

	/// <summary>
	/// With no container stack to merge into: split <c>moveUnits</c> units off the source into the container and leave the
	/// source at <c>keepUnits</c>. Uses <c>GearItem.InstantiateStackable</c>, which clones the item instead of loading its prefab.
	/// </summary>
	private static bool SplitAndMove(GearItem source, Container container, int moveUnits, int keepUnits, MelonLogger.Instance log, bool verbose)
	{
		string name = GearMath.SafeName(source) ?? "(未知物品)";

		int sourceUnits;
		try
		{
			sourceUnits = source.m_StackableItem.m_Units;
		}
		catch (Exception e)
		{
			log.Warning($"读取堆叠数量失败（{name}）：{e.Message}");
			return false;
		}

		if (moveUnits <= 0 || keepUnits <= 0 || sourceUnits != moveUnits + keepUnits)
		{
			return false;
		}

		GearItem? part;
		try
		{
			part = source.InstantiateStackable(moveUnits);
		}
		catch (Exception e)
		{
			log.Warning($"拆堆失败（{name}）：{e.Message}");
			return false;
		}

		if (part == null)
		{
			log.Warning($"拆堆失败（{name}）：InstantiateStackable 返回空。");
			return false;
		}

		try
		{
			StackableItem? ps = part.m_StackableItem;
			if (ps == null)
			{
				GearMath.Slay(part);
				GearMath.RestoreUnits(source, sourceUnits);
				log.Warning($"拆出来的 {name} 不是堆叠物品，已放弃。");
				return false;
			}

			ps.m_Units = moveUnits;
			if (ps.m_Units != moveUnits)
			{
				GearMath.Slay(part);
				GearMath.RestoreUnits(source, sourceUnits);
				log.Warning($"拆出来的 {name} 数量没设上，已放弃。");
				return false;
			}

			// The clone inherits the source's backpack flags, clear them before it enters the container
			part.m_InPlayerInventory = false;
			part.m_IsInSatchel = false;

			int partId = part.m_InstanceID;
			float partKg = GearMath.ItemKilograms(part);
			float before = GearMath.ToKilograms(container.GetTotalWeightKG());

			container.AddGear(part);

			float after = GearMath.ToKilograms(container.GetTotalWeightKG());
			bool accepted = ContainerHasInstance(container, partId) || after >= before + partKg * 0.5f;

			if (!accepted)
			{
				try
				{
					container.RemoveGear(part);
				}
				catch
				{
					// Best effort
				}

				GearMath.Slay(part);
				GearMath.RestoreUnits(source, sourceUnits);
				log.Warning($"容器没有接收拆出来的一份（{name}），已回滚。");
				return false;
			}

			GearMath.RestoreUnits(source, keepUnits);

			if (verbose)
			{
				log.Msg($"  ⇄ {name} {moveUnits}/{sourceUnits} 单独放进容器，背包留 {keepUnits}");
			}

			return true;
		}
		catch (Exception e)
		{
			try
			{
				container.RemoveGear(part);
			}
			catch
			{
				// Best effort
			}

			GearMath.Slay(part);
			GearMath.RestoreUnits(source, sourceUnits);
			log.Error($"拆堆迁移失败（{name}）：{e.Message}");
			return false;
		}
	}

	private static void ReturnToInventory(GearItem gi, Inventory inv, MelonLogger.Instance log)
	{
		try
		{
			if (gi == null)
			{
				return;
			}

			if (gi.m_InPlayerInventory)
			{
				return; // Already in the backpack; adding again would duplicate it
			}

			inv.AddGear(gi, false);
		}
		catch (Exception e)
		{
			log.Error("退回背包也失败，物品可能悬空：" + e.Message);
		}
	}

	private static bool ContainerHasInstance(Container container, int instanceId)
	{
		try
		{
			var items = container.m_Items;
			if (items == null)
			{
				return false;
			}

			for (int i = 0; i < items.Count; i++)
			{
				GearItem gi = items[i].m_GearItem;
				if (gi != null && gi.m_InstanceID == instanceId)
				{
					return true;
				}
			}
		}
		catch
		{
			// Best effort
		}

		return false;
	}

	// ---------------------------------------------------------------- Helpers

	private static bool SafeIsEnabled(Panel_Container panel)
	{
		try
		{
			return panel.IsEnabled();
		}
		catch
		{
			return false;
		}
	}

	private static string ContainerName(Container container)
	{
		try
		{
			return container.name;
		}
		catch
		{
			return "容器";
		}
	}

	/// <summary>
	/// Units that fit into the remaining capacity, floored and clamped to <c>maxUnits</c>. The float division is re-checked
	/// by multiplication, because 0.3 kg divides into 133.33 and rounding up would overfill the container.
	/// </summary>
	private static int FitUnits(float left, float unitKg, int maxUnits)
	{
		if (left <= Epsilon || unitKg <= Epsilon || maxUnits <= 0)
		{
			return 0;
		}

		int fit = (int)Math.Floor((left + 1e-4f) / unitKg);
		if (fit > maxUnits)
		{
			fit = maxUnits;
		}

		while (fit > 0 && fit * unitKg > left + Epsilon)
		{
			fit--;
		}

		return fit;
	}
}
