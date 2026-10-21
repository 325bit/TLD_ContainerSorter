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

		int moved = 0;
		int tooHeavy = 0;
		int failed = 0;
		float movedKg = 0f;

		foreach (GearItem gi in stock)
		{
			string? name = SafeName(gi);
			if (name == null)
			{
				continue;
			}

			if (!containerKinds.Contains(NormalizeName(name)))
			{
				continue; // 容器里没有这种物品
			}

			float itemKg = ItemKilograms(gi);
			float left = capacity - ToKilograms(container.GetTotalWeightKG());

			if (itemKg > left + Epsilon)
			{
				tooHeavy++;
				if (cfg.Verbose)
				{
					log.Msg($"  跳过 {name}（{itemKg:0.###}kg > 剩余 {left:0.###}kg）");
				}
				continue;
			}

			if (MoveOne(gi, container, inv, log, itemKg, cfg.Verbose))
			{
				moved++;
				movedKg += itemKg;
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

		try
		{
			panel.RefreshTables();
		}
		catch (Exception e)
		{
			log.Warning("刷新容器面板失败：" + e.Message);
		}

		float nowLeft = capacity - ToKilograms(container.GetTotalWeightKG());
		log.Msg($"整理完成：放入 {moved} 件（{movedKg:0.##}kg）"
			+ (tooHeavy > 0 ? $"，{tooHeavy} 件放不下" : string.Empty)
			+ (failed > 0 ? $"，{failed} 件失败" : string.Empty)
			+ $"，容器剩余 {nowLeft:0.##}kg");
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

	/// <summary>ItemWeight（内部以 long 记账）→ 公斤浮点。</summary>
	private static float ToKilograms(ItemWeight weight)
	{
		return weight / ItemWeight.FromKilograms(1f);
	}
}
