using System;
using System.Collections.Generic;
using Il2CppTLD.IntBackedUnit;
using MelonLoader;

namespace ContainerSorter;

/// <summary>
/// 容器整理的核心逻辑：
/// 容器面板打开时，把背包里「该容器中已经存在的同种物品」逐件搬进容器，
/// 直到容器剩余容量装不下下一件为止。
/// </summary>
internal static class ContainerTransfer
{
	/// <summary>kg 比较容差，避免浮点累积误差把刚好装满的物品挡在外面。</summary>
	private const float Epsilon = 0.0005f;

	internal static void Run(MelonLogger.Instance log, Settings cfg)
	{
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

		// 容器面板没开 → 什么都不做（不提示，避免每次按 R 都刷日志）
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

		float capacity = ToKilograms(container.m_Capacity);
		float used = ToKilograms(container.GetTotalWeightKG());
		float remaining = capacity - used;

		log.Msg($"[{ContainerName(container)}] 容量 {capacity:0.##}kg，已用 {used:0.##}kg，剩余 {remaining:0.##}kg");

		if (capacity <= 0f)
		{
			log.Warning("这个容器容量为 0，原版也不允许存放，跳过。");
			return;
		}

		if (remaining <= Epsilon)
		{
			log.Msg("容器已经装满，没有可迁移的空间。");
			return;
		}

		HashSet<string> containerKinds = CollectContainerKinds(container);
		if (containerKinds.Count == 0)
		{
			log.Msg("容器是空的，没有可以匹配的物品。");
			return;
		}

		List<GearItem> stock = CollectInventoryItems(inv, cfg);

		// 忽略清单按修改时间增量重读：改完存盘、下次按键即生效
		if (cfg.UseIgnoreList)
		{
			IgnoreList.Reload(log);
		}

		// 「保留（至少一个）」的物品先挑出要留在背包的那几个实例（默认 1 个，ID*数字 可指定 N 个）
		Dictionary<string, List<KeeperPick>> keepers = cfg.UseIgnoreList
			? PickKeepers(stock, containerKinds, cfg)
			: new Dictionary<string, List<KeeperPick>>(StringComparer.OrdinalIgnoreCase);

		// 迁移顺序：同一类物品里「整件要搬走的」排在「留在背包的」前面，
		// 同类内每份更重的先搬 —— 容器剩余空间只够一部分时，先塞进去的是重的那几块肉。
		// 不同种类之间的先后保持背包原序。
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
			string? name = SafeName(gi);
			if (name == null)
			{
				continue;
			}

			string kind = NormalizeName(name);
			if (!containerKinds.Contains(kind))
			{
				continue; // 容器里没有这种物品
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

			// keep > 0 表示这一件只搬走一部分，剩下的留在背包
			int keep = 0;
			if (rule.Mode == IgnoreMode.KeepOne)
			{
				bool isKeeper = TryGetKeeper(keepers, kind, gi, out int keeperUnits);

				if (!isKeeper)
				{
					keep = 0; // 同类里不需要留的那些整件搬走
				}
				else if (!stackable)
				{
					keep = 1; // 非堆叠物品：这一件本身留着
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
						float cond = Condition(gi);
						float unit = UnitKilograms(gi);
						log.Msg($"  = {name} 按「保留」留在背包（{units} 个"
							+ (cond >= 0f ? $"，耐久 {cond * 100f:0}%" : string.Empty)
							+ (unit > 0f ? $"，单份 {unit:0.##}kg" : string.Empty) + "）");
					}

					continue; // 没得搬
				}
			}

			int moveUnits = units - keep;
			float itemKg = ItemKilograms(gi);
			float unitKg = stackable && units > 0 ? itemKg / units : itemKg;
			float moveKg = keep > 0 ? unitKg * moveUnits : itemKg;

			float left = capacity - ToKilograms(container.GetTotalWeightKG());

			if (moveKg > left + Epsilon)
			{
				// 整组塞不下：能拆堆的就按剩余容量拆出一部分先搬，剩下的留在背包。
				// 只有「连一份都放不下」才真跳过 —— 100 张狼皮在 40kg 余量里能塞几十张，
				// 不能因为整组 50kg 就直接一张都不搬。
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

				keep = units - fitUnits;   // 装不下的部分继续留在背包
				moveUnits = fitUnits;
				moveKg = unitKg * moveUnits;
				partialMoves++;
			}

			bool ok;
			bool mergedNow = false;

			if (keep > 0)
			{
				// 只搬一部分：先试并进容器里已有的那一堆（source 本身就留在背包，只改数量），
				// 容器里没有可合并的堆就拆出一份新对象放进去。
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

				// 没得合 / 合并不成（比如重量账本不跟数量走）→ 退回常规迁移，至少东西能进容器
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

			if (capacity - ToKilograms(container.GetTotalWeightKG()) <= Epsilon)
			{
				break; // 满了
			}
		}

		RefreshPanels(panel, log);

		float nowLeft = capacity - ToKilograms(container.GetTotalWeightKG());
		log.Msg($"整理完成：放入 {moved} 件（{movedKg:0.##}kg）"
			+ (merged > 0 ? $"，其中 {merged} 件并进已有堆叠" : string.Empty)
			+ (ignored > 0 ? $"，{ignored} 件按忽略清单跳过" : string.Empty)
			+ (kept > 0 ? $"，{kept} 件按「保留」留在背包" : string.Empty)
			+ (partialMoves > 0 ? $"，{partialMoves} 堆因剩余容量只搬了一部分" : string.Empty)
			+ (tooHeavy > 0 ? $"，{tooHeavy} 件放不下" : string.Empty)
			+ (failed > 0 ? $"，{failed} 件失败" : string.Empty)
			+ $"，容器剩余 {nowLeft:0.##}kg");
	}

	/// <summary>
	/// 迁完刷新物品栏。本 mod 直接改数据、绕过了原版的 OnMoveItem 流程，
	/// 面板里的过滤列表和选中项还停在旧条目上，所以三件事都做：
	///   1. RefreshTables   —— 立刻重建容器表 + 背包表
	///   2. ForceRefresh    —— 置一次「强制重建」标记，下一帧 Update 会重算过滤列表/选中项
	///                        （只做 1 时，某些情况下会残留已经搬走的条目）
	///   3. Panel_Inventory.RefreshTable —— 背包面板自己的格子与负重显示
	/// 每一步单独 try：任何一步不可用都不影响其它两步，也不影响已经完成的迁移。
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
	/// 「保留（至少一个）」的每种物品挑出要留在背包的实例。只有容器里也有的种类才参与，其它种类本来就不会被搬。
	/// 留几个：清单里写 <c>ID*数字</c> 就是 N 个，不写默认 1 个；堆叠物品按份数累计，够数就停。
	/// 挑谁留：耐久低的先留（好状态收进容器）；耐久打平 → 单份更轻的先留（重的进容器，主要是肉类）。
	/// 关掉设置里的「保留时优先把高耐久放进容器」就退回老行为：留状态最好的。
	/// </summary>
	private static Dictionary<string, List<KeeperPick>> PickKeepers(List<GearItem> stock, HashSet<string> containerKinds, Settings cfg)
	{
		var result = new Dictionary<string, List<KeeperPick>>(StringComparer.OrdinalIgnoreCase);
		var candidates = new Dictionary<string, List<GearItem>>(StringComparer.OrdinalIgnoreCase);
		var order = new List<string>();

		foreach (GearItem gi in stock)
		{
			string? name = SafeName(gi);
			if (name == null)
			{
				continue;
			}

			string kind = NormalizeName(name);
			if (!containerKinds.Contains(kind) || IgnoreList.Lookup(kind).Mode != IgnoreMode.KeepOne)
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

			// 越靠前越该留在背包：耐久低 → 单份轻 → 实例 ID（保证每次结果一致）
			list.Sort((a, b) => CompareKeepPriority(a, b, cfg));

			var picks = new List<KeeperPick>();
			int remaining = need;

			foreach (GearItem gi in list)
			{
				if (remaining <= 0)
				{
					break;
				}

				int units = UnitsOf(gi);
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

	/// <summary>留在背包的一件/一堆，以及从它身上留几份。</summary>
	private readonly struct KeeperPick
	{
		internal KeeperPick(GearItem item, int keepUnits)
		{
			Item = item;
			KeepUnits = keepUnits;
		}

		internal GearItem Item { get; }

		internal int KeepUnits { get; }
	}

	private static bool TryGetKeeper(Dictionary<string, List<KeeperPick>> keepers, string kind, GearItem gi, out int keepUnits)
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

	/// <summary>排序用：结果为负表示 a 更该留在背包。</summary>
	private static int CompareKeepPriority(GearItem a, GearItem b, Settings cfg)
	{
		float condA = Condition(a);
		float condB = Condition(b);

		// 读不到耐久（没有 condition 的物品）就跳过耐久比较，直接比重量
		if (condA >= 0f && condB >= 0f && Math.Abs(condA - condB) > 0.001f)
		{
			return cfg.KeepBestInContainer
				? condA.CompareTo(condB)   // 留耐久低的
				: condB.CompareTo(condA);  // 留耐久高的
		}

		float kgA = UnitKilograms(a);
		float kgB = UnitKilograms(b);
		if (cfg.HeavierFirstWhenEqualCondition && kgA > 0f && kgB > 0f && Math.Abs(kgA - kgB) > 0.0001f)
		{
			return kgA.CompareTo(kgB); // 留单份更轻的
		}

		return InstanceId(a).CompareTo(InstanceId(b));
	}

	private static int InstanceId(GearItem gi)
	{
		try
		{
			return gi.m_InstanceID;
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>这一件算几份：堆叠就是 m_Units，非堆叠算 1。</summary>
	private static int UnitsOf(GearItem gi)
	{
		try
		{
			StackableItem? st = gi.m_StackableItem;
			if (st != null && st.m_Units > 0)
			{
				return st.m_Units;
			}
		}
		catch
		{
			// 读不到就当单件
		}

		return 1;
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
	/// 重排迁移顺序（同类分组的稳定排序）：
	///   1. 整件要搬走的排在「按保留留在背包」的前面，免得先占空间的是不搬的那件；
	///   2. 同类内按**单份重量**从重到轻，容器剩余空间不够时先塞进去的是重的那几块肉；
	///   3. 不同种类之间保持背包原序。
	/// </summary>
	private static List<GearItem> OrderForTransfer(List<GearItem> stock, Dictionary<string, List<KeeperPick>> keepers)
	{
		var groupRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		var keyed = new List<SortEntry>(stock.Count);

		foreach (GearItem gi in stock)
		{
			string? name = SafeName(gi);
			string kind = name == null ? "\u0000invalid" : NormalizeName(name);
			if (!groupRank.TryGetValue(kind, out int group))
			{
				group = groupRank.Count;
				groupRank[kind] = group;
			}

			keyed.Add(new SortEntry(gi, group, IsFullyKept(gi, kind, keepers) ? 1 : 0, UnitKilograms(gi)));
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

			return b.UnitKg.CompareTo(a.UnitKg); // 重的在前
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

	/// <summary>这一件是不是「整件都留在背包、什么都不搬」（只有这种才需要排到后面）。</summary>
	private static bool IsFullyKept(GearItem gi, string kind, Dictionary<string, List<KeeperPick>> keepers)
	{
		return TryGetKeeper(keepers, kind, gi, out int keepUnits) && keepUnits >= UnitsOf(gi);
	}

	// ---------------------------------------------------------------- 收集

	/// <summary>容器里现有物品的种类集合（按去掉 (Clone) 后的 prefab 名比较）。</summary>
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
				string? name = SafeName(gi);
				if (name != null)
				{
					kinds.Add(NormalizeName(name));
				}
			}
			catch
			{
				// 单个条目拿不到就跳过，不影响其它条目
			}
		}

		return kinds;
	}

	/// <summary>背包快照。先拍下来再迁移，避免边遍历边改集合。</summary>
	private static List<GearItem> CollectInventoryItems(Inventory inv, Settings cfg)
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
				// 主菜单/加载瞬间没有 PlayerManager，忽略
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

				// 水分/燃料这类特殊 supply 不参与，避免把内部虚拟物品搬来搬去
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
				// 单件拿不到就跳过
			}
		}

		return result;
	}

	// ---------------------------------------------------------------- 迁移

	private static bool MoveOne(GearItem gi, Container container, Inventory inv, MelonLogger.Instance log, float itemKg, bool verbose)
	{
		string name = SafeName(gi) ?? "(未知物品)";

		int instanceId;
		float before;
		try
		{
			instanceId = gi.m_InstanceID;
			before = ToKilograms(container.GetTotalWeightKG());
		}
		catch (Exception e)
		{
			log.Warning($"读物品状态失败（{name}）：{e.Message}");
			return false;
		}

		// 1) 先从背包摘下来（toContainer = true：不销毁、交给容器接手）
		try
		{
			inv.RemoveGear(gi.gameObject, true);
		}
		catch (Exception e)
		{
			log.Error($"从背包移出失败（{name}）：{e.Message}");
			return false;
		}

		// 2) 再挂到容器上（AddGear 内部会处理同类堆叠合并）
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

		// 3) 校验：物品要么出现在容器列表里，要么把容器总重顶上去了（说明并进了已有堆叠）
		float after;
		try
		{
			after = ToKilograms(container.GetTotalWeightKG());
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

		// 兜底：万一 RemoveGear 没把「在背包里」的标记清掉，会变成两边各一份
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
			// 已被并进容器里的同款堆叠（对象已销毁）时会走到这里，属正常
		}

		if (verbose)
		{
			log.Msg($"  + {name}  {itemKg:0.###}kg  →  容器 {after:0.##}kg");
		}

		return true;
	}

	// ---------------------------------------------------------------- 堆叠合并

	/// <summary>
	/// 在容器里找一坨能和 source 并成同一堆的堆叠。找不到返回 null（那就按常规搬过去，落成新的一堆）。
	/// 判定：同 prefab 种类 + 都是真堆叠 + 非液体 + 状态（condition）差值在原版允许范围内。
	/// </summary>
	private static GearItem? FindMergeTarget(Container container, GearItem source, string kind)
	{
		try
		{
			StackableItem srcStack = source.m_StackableItem;
			if (srcStack == null || srcStack.m_Units <= 0 || source.m_LiquidItem != null)
			{
				return null; // 不是堆叠物品；液体按体积记账，不掺和
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

				string? targetName = SafeName(target);
				if (targetName == null || !string.Equals(NormalizeName(targetName), kind, StringComparison.OrdinalIgnoreCase))
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
	/// 把 source 那一堆的数量并进容器里已存在的 target 那一堆，然后销毁 source。
	/// 走的是和普通迁移同一条「先移出背包」的路子，所以不会在容器里留下两个同款堆。
	/// </summary>
	private static bool MergeInto(GearItem source, GearItem target, Inventory inv, MelonLogger.Instance log, bool verbose)
	{
		string name = SafeName(source) ?? "(未知物品)";

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
			weightBefore = ToKilograms(target.GetItemWeightKG(false));
		}
		catch
		{
			weightBefore = -1f;
		}

		float expected;
		try
		{
			expected = ToKilograms(source.GetItemWeightKG(false));
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
			weightAfter = ToKilograms(target.GetItemWeightKG(false));
		}
		catch (Exception e)
		{
			log.Error($"并进容器堆叠失败（{name}）：{e.Message}");
			increased = false;
			weightAfter = weightBefore;
		}

		// 数量涨了，重量必须跟着涨「差不多一件物品那么多」：
		// 完全不涨 = 重量账本不看 m_Units；涨太多 = 重量已经含数量、再乘一次就重复计数。
		// 两种情况都说明不能硬来，回滚数量后走普通迁移（落成独立一堆）。
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
					target.m_StackableItem.m_Units = targetUnits; // 回滚数量
				}
				catch
				{
					// 忽略
				}

				log.Warning($"{name} 的堆叠重量和数量对不上（应该 +{expected:0.###}kg，实际 +{weightAfter - weightBefore:0.###}kg），已回滚，改为单独放一堆。");
			}

			ReturnToInventory(source, inv, log);
			return false;
		}

		// 数量已经转过去了，source 没有别处引用，直接销毁，避免容器里多出一堆
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

	// ---------------------------------------------------------------- 保留下限（只搬一部分）

	/// <summary>
	/// 只把 moveUnits 个并进容器已有的那一堆，source 仍然留在背包里、数量降到 keepUnits。
	/// 全程不碰背包的增删接口（物品本来就在背包里），只改两边的 m_Units，是最安全的一条路。
	/// 任意一步校验不过就双向回滚，返回 false 交给调用方换别的办法。
	/// </summary>
	private static bool MergeUnits(GearItem source, GearItem target, int moveUnits, int keepUnits, MelonLogger.Instance log, bool verbose)
	{
		string name = SafeName(source) ?? "(未知物品)";

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

		float targetBefore = SafeWeight(target);
		float sourceBefore = SafeWeight(source);
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
			RestoreUnits(source, sourceUnits);
			RestoreUnits(target, targetUnits);
			return false;
		}

		float targetAfter = SafeWeight(target);
		float sourceAfter = SafeWeight(source);

		// 容器那边涨的、背包那边掉下来的，都得差不多等于 moveUnits 份的重量
		bool grew = WeightMatches(targetBefore, targetAfter, expected);
		bool shrank = WeightMatches(sourceBefore, sourceAfter, -expected);

		if (!grew || !shrank)
		{
			RestoreUnits(source, sourceUnits);
			RestoreUnits(target, targetUnits);
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
	/// 容器里没有可合并的堆叠时：从 source 拆出一份 moveUnits 的新对象放进容器，source 留在背包里只剩 keepUnits。
	/// 拆堆用 <c>GearItem.InstantiateStackable</c>（复制自身，不走 prefab 加载，避开 bundle 阻塞那类坑）。
	/// </summary>
	private static bool SplitAndMove(GearItem source, Container container, int moveUnits, int keepUnits, MelonLogger.Instance log, bool verbose)
	{
		string name = SafeName(source) ?? "(未知物品)";

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
				Slay(part);
				RestoreUnits(source, sourceUnits);
				log.Warning($"拆出来的 {name} 不是堆叠物品，已放弃。");
				return false;
			}

			ps.m_Units = moveUnits;
			if (ps.m_Units != moveUnits)
			{
				Slay(part);
				RestoreUnits(source, sourceUnits);
				log.Warning($"拆出来的 {name} 数量没设上，已放弃。");
				return false;
			}

			// 复制出来的对象会带着原件的背包标记，进容器前清掉
			part.m_InPlayerInventory = false;
			part.m_IsInSatchel = false;

			int partId = part.m_InstanceID;
			float partKg = ItemKilograms(part);
			float before = ToKilograms(container.GetTotalWeightKG());

			container.AddGear(part);

			float after = ToKilograms(container.GetTotalWeightKG());
			bool accepted = ContainerHasInstance(container, partId) || after >= before + partKg * 0.5f;

			if (!accepted)
			{
				try
				{
					container.RemoveGear(part);
				}
				catch
				{
					// 忽略
				}

				Slay(part);
				RestoreUnits(source, sourceUnits);
				log.Warning($"容器没有接收拆出来的一份（{name}），已回滚。");
				return false;
			}

			RestoreUnits(source, keepUnits);

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
				// 忽略
			}

			Slay(part);
			RestoreUnits(source, sourceUnits);
			log.Error($"拆堆迁移失败（{name}）：{e.Message}");
			return false;
		}
	}

	/// <summary>数量改了以后核对重量变化是否符合预期（预期为负表示应该变轻）。抓不到基准重量就跳过校验。</summary>
	private static bool WeightMatches(float before, float after, float expected)
	{
		if (expected == 0f || before < 0f || after < 0f)
		{
			return true;
		}

		float delta = after - before;
		float lo = Math.Min(expected * 0.5f, expected * 2f);
		float hi = Math.Max(expected * 0.5f, expected * 2f);

		return delta >= lo - 0.0001f && delta <= hi + 0.0001f;
	}

	private static void RestoreUnits(GearItem gi, int units)
	{
		try
		{
			if (gi != null && gi.m_StackableItem != null)
			{
				gi.m_StackableItem.m_Units = units;
			}
		}
		catch
		{
			// 忽略
		}
	}

	private static void Slay(GearItem gi)
	{
		try
		{
			if (gi != null)
			{
				UnityEngine.Object.Destroy(gi.gameObject);
			}
		}
		catch
		{
			// 忽略
		}
	}

	/// <summary>物品总重量（kg）；读不到返回 -1，让校验自动跳过。</summary>
	private static float SafeWeight(GearItem gi)
	{
		try
		{
			return ToKilograms(gi.GetItemWeightKG(false));
		}
		catch
		{
			return -1f;
		}
	}

	private static float Condition(GearItem gi)
	{
		try
		{
			return gi.GetNormalizedCondition();
		}
		catch
		{
			return -1f;
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
				return; // 本来还在背包里，别重复加
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
			// 忽略
		}

		return false;
	}

	// ---------------------------------------------------------------- 工具

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

	private static string? SafeName(GearItem gi)
	{
		if (gi == null)
		{
			return null;
		}

		try
		{
			return gi.name;
		}
		catch
		{
			return null;
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

	/// <summary>Unity 实例名会带 (Clone) 后缀，统一去掉再比较。</summary>
	private static string NormalizeName(string name)
	{
		string n = name.Trim();
		if (n.EndsWith("(Clone)", StringComparison.Ordinal))
		{
			n = n.Substring(0, n.Length - "(Clone)".Length).Trim();
		}

		return n;
	}

	private static float ItemKilograms(GearItem gi)
	{
		try
		{
			return ToKilograms(gi.GetItemWeightKG(false));
		}
		catch
		{
			return 0f;
		}
	}

	/// <summary>
	/// 剩余容量最多能装下多少份（向下取整，并夹到 maxUnits）。
	/// 先按浮点算一次再用乘法回验，避免 0.3kg 这类重量除出 133.33… 时多算一份把容器撑爆。
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

	/// <summary>单份重量：一堆 20 支箭按每支摊开，非堆叠就是整件重量。比较「谁更重」用这个。</summary>
	private static float UnitKilograms(GearItem gi)
	{
		float kg = ItemKilograms(gi);
		try
		{
			StackableItem? st = gi.m_StackableItem;
			if (st != null && st.m_Units > 1 && kg > 0f)
			{
				return kg / st.m_Units;
			}
		}
		catch
		{
			// 读不到堆叠信息就按整件算
		}

		return kg;
	}

	/// <summary>ItemWeight（内部以 long 记账）→ 公斤浮点。</summary>
	private static float ToKilograms(ItemWeight weight)
	{
		return weight / ItemWeight.FromKilograms(1f);
	}
}
