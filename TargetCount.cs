using System;
using System.Collections.Generic;
using MelonLoader;

namespace ContainerSorter;

/// <summary>
/// Take-out half of the target-count rule (<c>ID*count</c>): when the backpack holds fewer than N units, take the
/// difference back out of the open container. The other direction lives in <see cref="ContainerTransfer"/>, which reads
/// the same <see cref="IgnoreList.Lookup"/> rule, so the list is parsed in exactly one place.
/// Only kinds the container actually holds are topped up — a label never conjures items out of nothing.
/// </summary>
internal static class TargetCount
{
	/// <summary>
	/// Walks the target counts and takes the missing units back out of <paramref name="container"/>.
	/// Must run before the move-in pass: it takes items out, which frees the space they need.
	/// </summary>
	/// <param name="movedUnits">Total units actually taken back.</param>
	/// <param name="filledKinds">Kinds that received at least one unit.</param>
	internal static void Fill(Container container, Inventory inv, MelonLogger.Instance log, Settings cfg, out int movedUnits, out int filledKinds)
	{
		movedUnits = 0;
		filledKinds = 0;

		IReadOnlyDictionary<string, int> targets = IgnoreList.Targets;
		if (targets.Count == 0)
		{
			return;
		}

		int shortKinds = 0;

		foreach (KeyValuePair<string, int> kv in targets)
		{
			string kind = kv.Key;
			int target = kv.Value;
			if (target <= 0)
			{
				continue;
			}

			int current = CountInInventory(inv, kind);
			if (current >= target)
			{
				continue; // At or above target: the surplus is the move-in pass's job
			}

			List<GearItem> sources = CollectContainerStacks(container, kind);
			if (sources.Count == 0)
			{
				shortKinds++;
				if (cfg.Verbose)
				{
					log.Msg($"  · {kind} 背包 {current}/{target}，容器里没有这种物品，不取");
				}

				continue;
			}

			int need = target - current;
			int got = 0;

			foreach (GearItem src in sources)
			{
				if (need <= 0)
				{
					break;
				}

				int units = GearMath.UnitsOf(src);
				if (units <= 0)
				{
					continue;
				}

				int take = Math.Min(units, need);
				bool ok = take >= units
					? MoveWhole(src, container, inv, log, cfg.Verbose)
					: TakePart(src, inv, kind, take, log, cfg.Verbose);

				if (!ok)
				{
					continue; // Try the next stack; one failure must not abandon the whole kind
				}

				got += take;
				need -= take;
			}

			if (got > 0)
			{
				movedUnits += got;
				filledKinds++;
			}

			if (need > 0)
			{
				shortKinds++;
			}

			if (cfg.Verbose)
			{
				log.Msg($"  ← {kind} 背包 {current} → {current + got}（目标 {target}）"
					+ (need > 0 ? $"，仍差 {need} 份" : string.Empty));
			}
		}

		if (movedUnits > 0 || shortKinds > 0)
		{
			log.Msg($"目标数量：从容器取回 {movedUnits} 份（{filledKinds} 种）"
				+ (shortKinds > 0 ? $"，{shortKinds} 种仍不够（容器里没有或数量不足）" : string.Empty));
		}
	}

	/// <summary>
	/// Total units of a kind in the backpack, deliberately including worn, held and satchel items: the skip settings
	/// decide what may be moved, not how many the player owns. Counting only movable instances would read a held tool
	/// as zero and pull a spare one out of the container.
	/// </summary>
	private static int CountInInventory(Inventory inv, string kind)
	{
		var items = inv.m_Items;
		if (items == null)
		{
			return 0;
		}

		int total = 0;

		for (int i = 0; i < items.Count; i++)
		{
			try
			{
				GearItem gi = items[i].m_GearItem;
				if (gi == null || gi.m_IsHidden || gi.m_WaterSupply != null)
				{
					continue;
				}

				string? name = GearMath.SafeName(gi);
				if (name == null || !IsKind(name, kind))
				{
					continue;
				}

				int units = GearMath.UnitsOf(gi);
				if (units > 0)
				{
					total += units;
				}
			}
			catch
			{
				// Unreadable instance: skip it, keep counting the rest
			}
		}

		return total;
	}

	/// <summary>
	/// Every stack of that kind in the container, best condition first so the player gets the good copy,
	/// then the smallest stack, which means fewer objects have to be split.
	/// </summary>
	private static List<GearItem> CollectContainerStacks(Container container, string kind)
	{
		var result = new List<GearItem>();

		var items = container.m_Items;
		if (items == null)
		{
			return result;
		}

		for (int i = 0; i < items.Count; i++)
		{
			try
			{
				GearItem gi = items[i].m_GearItem;
				if (gi == null)
				{
					continue;
				}

				string? name = GearMath.SafeName(gi);
				if (name == null || !IsKind(name, kind) || GearMath.UnitsOf(gi) <= 0)
				{
					continue;
				}

				result.Add(gi);
			}
			catch
			{
				// Unreadable instance: skip it
			}
		}

		if (result.Count > 1)
		{
			result.Sort((a, b) => CompareSourcePriority(a, b));
		}

		return result;
	}

	/// <summary>Negative means <paramref name="a"/> is taken first: higher condition wins, then the smaller stack.</summary>
	private static int CompareSourcePriority(GearItem a, GearItem b)
	{
		float condA = GearMath.Condition(a);
		float condB = GearMath.Condition(b);

		if (condA >= 0f && condB >= 0f && Math.Abs(condA - condB) > 0.001f)
		{
			return condB.CompareTo(condA);
		}

		return GearMath.UnitsOf(a).CompareTo(GearMath.UnitsOf(b));
	}

	// ---------------------------------------------------------------- Take actions

	/// <summary>
	/// Takes the whole stack out through the vanilla container API, which handles the backpack-side flags and notices.
	/// Afterwards the item must actually be in the backpack (instance found, or total weight rose by roughly the stack);
	/// if not, add it explicitly, and if even that fails put it back into the container.
	/// </summary>
	private static bool MoveWhole(GearItem gi, Container container, Inventory inv, MelonLogger.Instance log, bool verbose)
	{
		string name = GearMath.SafeName(gi) ?? "(未知物品)";
		int units = GearMath.UnitsOf(gi);
		float itemKg = GearMath.ItemKilograms(gi);
		int instanceId = GearMath.InstanceId(gi);
		float weightBefore = InventoryKilograms(inv);

		try
		{
			container.RemoveGear(gi, true);
		}
		catch (Exception e)
		{
			log.Error($"从容器取出失败（{name}）：{e.Message}");
			return false;
		}

		if (!LandedInInventory(inv, instanceId, weightBefore, itemKg))
		{
			try
			{
				inv.AddGear(gi, false);
			}
			catch (Exception e)
			{
				log.Error($"放入背包失败（{name}）：{e.Message}");
			}

			if (!LandedInInventory(inv, instanceId, weightBefore, itemKg))
			{
				log.Warning($"背包没有接收从容器取出的 {name}，尝试放回容器。");

				try
				{
					container.AddGear(gi);
				}
				catch (Exception e)
				{
					log.Error($"放回容器也失败，{name} 可能悬空：{e.Message}");
				}

				return false;
			}
		}

		if (verbose)
		{
			log.Msg($"  ← {name} ×{units} 整堆从容器取回（{itemKg:0.###}kg）");
		}

		return true;
	}

	/// <summary>
	/// Takes only part of a stack: shift units into a compatible backpack stack, or split a new one off.
	/// Either path has to raise the backpack unit count for real; if it does not, nothing is moved.
	/// </summary>
	private static bool TakePart(GearItem source, Inventory inv, string kind, int take, MelonLogger.Instance log, bool verbose)
	{
		GearItem? target = FindInventoryStack(inv, source, kind);
		if (target != null && ShiftUnits(source, target, take, log, verbose))
		{
			return true;
		}

		return InstantiatePart(source, inv, take, log, verbose);
	}

	/// <summary>
	/// Shifts <paramref name="take"/> units between a container stack and a backpack stack by editing <c>m_Units</c>
	/// only, never the add/remove API. Both weight deltas must match the weight of those units, otherwise both sides are
	/// rolled back — that check catches a weight ledger another mod has rewritten, which would otherwise skew encumbrance.
	/// </summary>
	private static bool ShiftUnits(GearItem source, GearItem target, int take, MelonLogger.Instance log, bool verbose)
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
			log.Warning($"读取堆叠份数失败（{name}）：{e.Message}");
			return false;
		}

		// Whole stacks go through MoveWhole; here a part must stay behind, and any unit mismatch is refused
		if (take <= 0 || take >= sourceUnits || targetUnits <= 0)
		{
			return false;
		}

		float sourceBefore = GearMath.SafeWeight(source);
		float targetBefore = GearMath.SafeWeight(target);
		float expected = sourceBefore > 0f ? sourceBefore * take / sourceUnits : 0f;

		bool applied;
		try
		{
			source.m_StackableItem.m_Units = sourceUnits - take;
			target.m_StackableItem.m_Units = targetUnits + take;
			applied = source.m_StackableItem.m_Units == sourceUnits - take
				&& target.m_StackableItem.m_Units == targetUnits + take;
		}
		catch (Exception e)
		{
			log.Error($"挪动堆叠份数失败（{name}）：{e.Message}");
			applied = false;
		}

		bool shrank = GearMath.WeightMatches(sourceBefore, GearMath.SafeWeight(source), -expected);
		bool grew = GearMath.WeightMatches(targetBefore, GearMath.SafeWeight(target), expected);
		float sourceDelta = GearMath.SafeWeight(source) - sourceBefore;
		float targetDelta = GearMath.SafeWeight(target) - targetBefore;

		if (!applied || !shrank || !grew)
		{
			GearMath.RestoreUnits(source, sourceUnits);
			GearMath.RestoreUnits(target, targetUnits);

			if (applied)
			{
				log.Warning($"{name} 的堆叠重量没有随份数变化（容器 {sourceDelta:+0.###;-0.###;0}kg / "
					+ $"背包 {targetDelta:+0.###;-0.###;0}kg，期望 ±{expected:0.###}kg），已回滚。");
			}

			return false;
		}

		if (verbose)
		{
			log.Msg($"  ← {name} {take}/{sourceUnits} 从容器并进背包已有堆叠（{targetUnits} → {targetUnits + take}）");
		}

		return true;
	}

	/// <summary>
	/// With no backpack stack to merge into: split a <paramref name="take"/>-unit copy off the container stack and add it
	/// to the backpack, then deduct those units from the container. Order is deduct, add, verify; any failure restores the
	/// container units with <see cref="GearMath.RestoreUnits"/>, so the same units never exist on both sides.
	/// </summary>
	private static bool InstantiatePart(GearItem source, Inventory inv, int take, MelonLogger.Instance log, bool verbose)
	{
		string name = GearMath.SafeName(source) ?? "(未知物品)";

		int sourceUnits;
		try
		{
			sourceUnits = source.m_StackableItem.m_Units;
		}
		catch (Exception e)
		{
			log.Warning($"读取堆叠份数失败（{name}）：{e.Message}");
			return false;
		}

		if (take <= 0 || take >= sourceUnits)
		{
			return false;
		}

		GearItem? part;
		try
		{
			part = source.InstantiateStackable(take);
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
				log.Warning($"拆出来的 {name} 不是堆叠物品，已放弃。");
				return false;
			}

			ps.m_Units = take;
			if (ps.m_Units != take)
			{
				GearMath.Slay(part);
				log.Warning($"拆出来的 {name} 份数没设上，已放弃。");
				return false;
			}

			// The clone inherits the source's container and backpack flags; AddGear maintains them
			part.m_InPlayerInventory = false;
			part.m_IsInSatchel = false;

			int partId = GearMath.InstanceId(part);
			float partKg = GearMath.ItemKilograms(part);
			float invBefore = InventoryKilograms(inv);

			// Deduct from the container first: if that fails nothing has touched the backpack yet
			source.m_StackableItem.m_Units = sourceUnits - take;
			if (source.m_StackableItem.m_Units != sourceUnits - take)
			{
				GearMath.Slay(part);
				log.Warning($"容器里的 {name} 份数扣不下来，已放弃这次取回。");
				return false;
			}

			inv.AddGear(part, false);

			if (!LandedInInventory(inv, partId, invBefore, partKg))
			{
				GearMath.RestoreUnits(source, sourceUnits);
				GearMath.Slay(part);
				log.Warning($"背包没有接收拆出来的 {name}（{take} 份），容器份额已还原。");
				return false;
			}

			if (verbose)
			{
				log.Msg($"  ← {name} {take}/{sourceUnits} 从容器拆出，单独放进背包");
			}

			return true;
		}
		catch (Exception e)
		{
			GearMath.RestoreUnits(source, sourceUnits);
			GearMath.Slay(part);
			log.Error($"从容器拆堆失败（{name}）：{e.Message}");
			return false;
		}
	}

	/// <summary>
	/// The backpack stack that the taken units can join: same prefab, both real stacks, no liquid, and a condition gap the
	/// vanilla limit allows. Returns null when there is none, and the caller splits a new stack instead.
	/// </summary>
	private static GearItem? FindInventoryStack(Inventory inv, GearItem source, string kind)
	{
		try
		{
			StackableItem? srcStack = source.m_StackableItem;
			if (srcStack == null || source.m_LiquidItem != null)
			{
				return null; // Not a stack; liquids are accounted by volume and stay out of this
			}

			float maxDiff = srcStack.m_StackConditionDifferenceConstraint;
			if (maxDiff <= 0f)
			{
				maxDiff = 5f; // Vanilla leaves 0 in places; 5% keeps fresh food away from nearly rotten food
			}

			float condition = source.GetNormalizedCondition();
			int sourceId = GearMath.InstanceId(source);

			var items = inv.m_Items;
			if (items == null)
			{
				return null;
			}

			GearItem? best = null;
			float bestDiff = float.MaxValue;

			for (int i = 0; i < items.Count; i++)
			{
				GearItem target = items[i].m_GearItem;
				if (target == null || target.m_IsHidden || target.m_LiquidItem != null)
				{
					continue;
				}

				if (GearMath.InstanceId(target) == sourceId)
				{
					continue;
				}

				StackableItem tStack = target.m_StackableItem;
				if (tStack == null || tStack.m_Units <= 0)
				{
					continue;
				}

				string? targetName = GearMath.SafeName(target);
				if (targetName == null || !IsKind(targetName, kind))
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

	// ---------------------------------------------------------------- Helpers

	/// <summary>
	/// Whether an item is the kind named in the list, compared by prefab name with the (Clone) suffix removed.
	/// The list may omit the <c>GEAR_</c> prefix and this accepts that too, otherwise <c>Arrow*20</c> would match nothing.
	/// </summary>
	private static bool IsKind(string name, string kind)
	{
		string normalized = GearMath.NormalizeName(name);
		if (string.Equals(normalized, kind, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return normalized.StartsWith("GEAR_", StringComparison.OrdinalIgnoreCase)
			&& normalized.Length > 5
			&& string.Equals(normalized.Substring(5), kind, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Whether the item really landed in the backpack: instance found, or total weight rose by about one item (merging destroys the instance).</summary>
	private static bool LandedInInventory(Inventory inv, int instanceId, float weightBefore, float itemKg)
	{
		try
		{
			if (instanceId != 0 && inv.FindByInstanceID(instanceId) != null)
			{
				return true;
			}
		}
		catch
		{
			// Not found: fall through to the weight check
		}

		float after = InventoryKilograms(inv);
		return weightBefore >= 0f && after >= weightBefore + itemKg * 0.5f;
	}

	/// <summary>Total backpack weight in kg; -1 when unreadable so the check is skipped.</summary>
	private static float InventoryKilograms(Inventory inv)
	{
		try
		{
			return GearMath.ToKilograms(inv.GetTotalWeightKG());
		}
		catch
		{
			return -1f;
		}
	}
}
