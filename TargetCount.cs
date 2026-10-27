using System;
using System.Collections.Generic;
using MelonLoader;

namespace ContainerSorter;

/// <summary>
/// 「目标数量」规则（清单里的 <c>物品ID*数字</c>）的出场方向：背包里该物品少于 N 份时，从当前打开的容器取回差额。
/// 反方向（背包多于 N 份时搬进容器）由 <see cref="ContainerTransfer"/> 的既有搬入流程按同一份规则完成，
/// 所以两边都只认 <see cref="IgnoreList.Lookup"/> 的结果，规则解析只有一处。
/// 只在容器里已经有这种物品时动作 —— 不会凭标签把容器里没有的种类变出来。
/// </summary>
internal static class TargetCount
{
	/// <summary>
	/// 逐条核对目标数量，把背包不够的份数从 <paramref name="container"/> 取回。
	/// 必须在搬入流程之前调用：这一步是从容器往外拿，顺手腾出空间给后面的搬入用。
	/// </summary>
	/// <param name="movedUnits">实际取回的份数合计。</param>
	/// <param name="filledKinds">发生取回的种类数。</param>
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
				continue; // 刚好够 / 超出：多出来的那部分交给搬入流程
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
					continue; // 这一堆动不了就换下一堆，别让一次失败毁掉整类
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
	/// 背包里该种物品的总份数。口径刻意包含穿戴中 / 手持 / 小包内的实例：
	/// 设置项只决定「能不能搬」，不该影响「背包里到底有几个」，否则拿在手上的工具会被算成 0 份、
	/// 白白多取一件回来。
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
				// 单件读不到就跳过，不影响其它条目
			}
		}

		return total;
	}

	/// <summary>
	/// 容器里该种物品的每一堆。状态好的排在前面（先给玩家最好的那份），
	/// 状态打平时先搬小堆，少拆几次对象。
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
				// 单件读不到就跳过
			}
		}

		if (result.Count > 1)
		{
			result.Sort((a, b) => CompareSourcePriority(a, b));
		}

		return result;
	}

	/// <summary>结果为负表示 a 该先被取走：耐久高的优先，其次份数少的优先。</summary>
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

	// ---------------------------------------------------------------- 取回动作

	/// <summary>
	/// 整堆都取回：走原版「从容器取出」的接口，背包侧该有的标记与通知都由游戏自己处理。
	/// 取完核对物品确实到了背包（实例在列表里，或背包总重涨了约一堆），没到就退回背包接口、再不行放回容器。
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
	/// 只取一部分：背包里有同款且状态接近的堆就挪份数，没有就拆一份新的出来。
	/// 两条路都得让背包份数真的涨上去，涨不上就原样退回。
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
	/// 在容器堆和背包堆之间挪 <paramref name="take"/> 份：只改两边的 m_Units，不碰任何增删接口。
	/// 改完核对「容器轻了、背包重了」都约等于这几份的重量，不符就双向回滚（同一份物品的重量账本
	/// 在别的 mod 里被改过时，这一步能挡住算错的负重）。
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

		// 取整堆走 MoveWhole；这里只处理「容器里还留一部分」，份数账对不上一律不动
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
	/// 背包里没有可合并的堆时：从容器那一堆拆一份 <paramref name="take"/> 份的新对象放进背包，
	/// 再把源堆份数扣掉。顺序是「先扣容器、再进背包、最后核对」，任何一步不成都用 <see cref="GearMath.RestoreUnits"/>
	/// 把容器份额还原，绝不让两边各留一份。
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

			// 复制出来的对象会带着源件的容器/背包标记，交给 AddGear 自己维护
			part.m_InPlayerInventory = false;
			part.m_IsInSatchel = false;

			int partId = GearMath.InstanceId(part);
			float partKg = GearMath.ItemKilograms(part);
			float invBefore = InventoryKilograms(inv);

			// 容器先扣：扣不动就直接放弃，背包那边还没被动过
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
	/// 背包里能和新取出的份数并成同一堆的那一堆：同 prefab、都是真堆叠、非液体、状态差值在原版允许范围内。
	/// 找不到返回 null，调用方改走「拆一份新的」。
	/// </summary>
	private static GearItem? FindInventoryStack(Inventory inv, GearItem source, string kind)
	{
		try
		{
			StackableItem? srcStack = source.m_StackableItem;
			if (srcStack == null || source.m_LiquidItem != null)
			{
				return null; // 不是堆叠物品；液体按体积记账，不掺和
			}

			float maxDiff = srcStack.m_StackConditionDifferenceConstraint;
			if (maxDiff <= 0f)
			{
				maxDiff = 5f; // 原版没填时给 5% 的宽容度
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

	// ---------------------------------------------------------------- 工具

	/// <summary>
	/// 物品是不是清单里写的那种（按去掉 (Clone) 后的 prefab 名比较）。
	/// 清单允许省略 <c>GEAR_</c> 前缀，这里跟着放行 —— 否则写 <c>Arrow*20</c> 会一条都对不上。
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

	/// <summary>物品确实落到背包里了：实例能被查到，或背包总重涨了约一件物品那么多（合堆时实例会被销毁）。</summary>
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
			// 查不到就算没到，走重量判断
		}

		float after = InventoryKilograms(inv);
		return weightBefore >= 0f && after >= weightBefore + itemKg * 0.5f;
	}

	/// <summary>背包总重（kg）；读不到返回 -1，让校验自动跳过。</summary>
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
