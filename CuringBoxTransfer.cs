using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace ContainerSorter;

/// <summary>
/// Move-in pass for the curing box (<c>INTERACTIVE_CURINGBOX</c>), which the regular path cannot handle: the box is a
/// <see cref="CuringStorage"/> (a <see cref="DecorationItem"/>) with no <see cref="Container"/> component, and it opens
/// <see cref="Panel_CuringStorage"/> instead of <see cref="Panel_Container"/>.
/// Stored meat lives in <see cref="CuringStorage.CuringItems"/>; salt has its own storage and its own weight, so the
/// capacity read here covers meat only.
/// </summary>
internal static class CuringBoxTransfer
{
	private const float Epsilon = 0.0005f;

	/// <summary>
	/// Runs the curing-box pass when that panel is open.
	/// Returns false when it is not (the caller should try the container path), true when this press is accounted for.
	/// </summary>
	internal static bool Run(MelonLogger.Instance log, Settings cfg)
	{
		Panel_CuringStorage panel;
		try
		{
			panel = InterfaceManager.GetPanel<Panel_CuringStorage>();
		}
		catch (Exception e)
		{
			log.Warning("取 Panel_CuringStorage 失败：" + e.Message);
			return false;
		}

		if (panel == null || !IsEnabled(panel))
		{
			return false; // Another panel is open; stay silent
		}

		if (!cfg.UseCuringBox)
		{
			return true; // The panel is ours but the feature is off: do nothing, do not fall through
		}

		CuringStorage? box = panel.m_CuringStorage;
		if (box == null)
		{
			log.Warning("腌肉盒面板已打开，但 m_CuringStorage 为空，跳过。");
			return true;
		}

		Inventory inv = GameManager.GetInventoryComponent();
		if (inv == null)
		{
			log.Warning("拿不到 Inventory 组件，跳过。");
			return true;
		}

		// The list reloads itself when its timestamp changes: save the file, press the key again
		if (cfg.UseIgnoreList)
		{
			IgnoreList.Reload(log);
		}

		float capacity = GearMath.ToKilograms(box.m_MaxStorage);
		float used = GearMath.ToKilograms(box.TotalWeight);

		log.Msg($"[腌肉盒] 容量 {capacity:0.##}kg，已用 {used:0.##}kg，剩余 {Math.Max(0f, capacity - used):0.##}kg（盐仓另算）");

		if (capacity <= 0f)
		{
			log.Warning("这个腌肉盒容量为 0，跳过搬入。");
			return true;
		}

		if (capacity - used <= Epsilon)
		{
			log.Msg("腌肉盒已经装满，没有可迁移的空间。");
			return true;
		}

		HashSet<string> boxKinds = CollectKinds(box);
		if (boxKinds.Count == 0)
		{
			log.Msg("腌肉盒是空的，没有可以匹配的物品。");
			return true;
		}

		List<GearItem> stock = ContainerTransfer.CollectInventoryItems(inv, cfg);

		// Same rules as the container path: "keep N" decides which instances stay behind
		Dictionary<string, List<ContainerTransfer.KeeperPick>> keepers = cfg.UseIgnoreList
			? ContainerTransfer.PickKeepers(stock, boxKinds, cfg)
			: new Dictionary<string, List<ContainerTransfer.KeeperPick>>(StringComparer.OrdinalIgnoreCase);

		if (cfg.HeavierFirstWhenEqualCondition)
		{
			stock = ContainerTransfer.OrderForTransfer(stock, keepers);
		}

		int moved = 0;
		int kept = 0;
		int ignored = 0;
		int tooHeavy = 0;
		int stacked = 0;
		int failed = 0;
		float movedKg = 0f;

		foreach (GearItem gi in stock)
		{
			string? name = GearMath.SafeName(gi);
			if (name == null)
			{
				continue;
			}

			string kind = GearMath.NormalizeName(name);
			if (!boxKinds.Contains(kind))
			{
				continue; // The box holds no item of this kind
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
			try
			{
				StackableItem? st = gi.m_StackableItem;
				if (st != null && st.m_Units > 0)
				{
					units = st.m_Units;
				}
			}
			catch
			{
				continue;
			}

			// The box records one curing entry per object; a stack would be stored as a single entry and cure as one
			if (units > 1)
			{
				stacked++;
				if (cfg.Verbose)
				{
					log.Msg($"  × {name} 是 {units} 个一叠，腌肉盒只收单件，跳过");
				}

				continue;
			}

			if (ContainerTransfer.IsKeepRule(rule.Mode)
				&& ContainerTransfer.TryGetKeeper(keepers, kind, gi, out _))
			{
				kept++;
				if (cfg.Verbose)
				{
					float cond = GearMath.Condition(gi);
					log.Msg($"  = {name} 按「保留」留在背包"
						+ (cond >= 0f ? $"（耐久 {cond * 100f:0}%）" : string.Empty));
				}

				continue;
			}

			float itemKg = GearMath.ItemKilograms(gi);
			float left = capacity - used;

			// CuringStorage.AddItem validates nothing, so the capacity gate has to live here
			if (itemKg > left + Epsilon)
			{
				tooHeavy++;
				if (cfg.Verbose)
				{
					log.Msg($"  跳过 {name}（{itemKg:0.###}kg > 剩余 {left:0.###}kg）");
				}

				continue;
			}

			if (MoveInto(gi, box, inv, log, itemKg))
			{
				moved++;
				movedKg += itemKg;
				used += itemKg; // TotalWeight only refreshes in Update, so keep a local running total

				if (cfg.Verbose)
				{
					log.Msg($"  + {name}  {itemKg:0.###}kg  →  腌肉盒 {used:0.##}kg");
				}
			}
			else
			{
				failed++;
			}

			if (capacity - used <= Epsilon)
			{
				break; // Full
			}
		}

		Refresh(panel, log);

		float nowLeft = capacity - used;
		log.Msg($"腌肉盒整理完成：放入 {moved} 件（{movedKg:0.##}kg）"
			+ (ignored > 0 ? $"，{ignored} 件按忽略清单跳过" : string.Empty)
			+ (kept > 0 ? $"，{kept} 件按「保留」留在背包" : string.Empty)
			+ (stacked > 0 ? $"，{stacked} 堆因成叠未拆被跳过" : string.Empty)
			+ (tooHeavy > 0 ? $"，{tooHeavy} 件放不下" : string.Empty)
			+ (failed > 0 ? $"，{failed} 件失败" : string.Empty)
			+ $"，剩余 {Math.Max(0f, nowLeft):0.##}kg");

		return true;
	}

	/// <summary>
	/// Moves one object into the box. Follows the vanilla order (<c>Panel_CuringStorage.HandleOnItemTransfer</c>):
	/// <see cref="CuringStorage.AddItem"/> parents the object under the box first, then the backpack gives it up.
	/// Reversing the two leaves the object detached from both sides when the second step fails.
	/// </summary>
	private static bool MoveInto(GearItem gi, CuringStorage box, Inventory inv, MelonLogger.Instance log, float itemKg)
	{
		string name = GearMath.SafeName(gi) ?? "(未知物品)";

		int before = SafeCount(box);
		float weightBefore = GearMath.ToKilograms(box.TotalWeight);

		try
		{
			box.AddItem(gi);
		}
		catch (Exception e)
		{
			log.Error($"放入腌肉盒失败（{name}）：{e.Message}");
			return false;
		}

		try
		{
			inv.RemoveGear(gi.gameObject, true);
		}
		catch (Exception e)
		{
			log.Warning($"从背包移出 {name} 失败，再试一次：{e.Message}");

			try
			{
				inv.RemoveGear(gi.gameObject);
			}
			catch (Exception again)
			{
				log.Error($"仍无法从背包移出 {name}：{again.Message}");
				return false;
			}
		}

		// AddItem has no return value and does not validate anything, so confirm an entry landed
		bool listed = SafeCount(box) > before;
		bool heavier = GearMath.ToKilograms(box.TotalWeight) >= weightBefore + itemKg - Epsilon;

		if (!listed && !heavier)
		{
			log.Warning($"腌肉盒没有接收 {name}，已退回背包。");
			ReturnToBackpack(gi, log);
			return false;
		}

		// Safety net: a stale in-inventory flag would leave a copy on both sides
		try
		{
			if (gi.m_InPlayerInventory)
			{
				log.Warning($"{name} 仍在背包标记里，补一次移出。");
				inv.RemoveGear(gi.gameObject, true);
			}
		}
		catch
		{
			// Best effort: the object is already detached
		}

		return true;
	}

	/// <summary>
	/// Kinds the box already holds. Only those are moved in: the game itself does not check what may be cured
	/// (<see cref="CuringStorage.AddItem"/> accepts anything), so matching an existing kind keeps this mod inside
	/// whatever the player already put there instead of inventing an allow list.
	/// </summary>
	private static HashSet<string> CollectKinds(CuringStorage box)
	{
		var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		try
		{
			var items = box.CuringItems;
			if (items == null)
			{
				return kinds;
			}

			for (int i = 0; i < items.Count; i++)
			{
				try
				{
					string? name = GearMath.SafeName(items[i].m_InstantiatedItem);
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
		}
		catch (Exception)
		{
			// Fall back to "nothing to match", the caller reports an empty box
		}

		return kinds;
	}

	private static int SafeCount(CuringStorage box)
	{
		try
		{
			var items = box.CuringItems;
			return items == null ? 0 : items.Count;
		}
		catch
		{
			return 0;
		}
	}

	private static bool IsEnabled(Panel_CuringStorage panel)
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

	/// <summary>
	/// Rebuilds both tables. The panel owns that work (<see cref="Panel_CuringStorage.ForceRefreshTables"/>); this mod
	/// edits the data directly, so vanilla never runs its own handler and the lists would keep the stale entries.
	/// </summary>
	private static void Refresh(Panel_CuringStorage panel, MelonLogger.Instance log)
	{
		try
		{
			panel.ForceRefreshTables();
		}
		catch (Exception e)
		{
			log.Warning("刷新腌肉盒面板失败：" + e.Message);
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

	private static void ReturnToBackpack(GearItem gi, MelonLogger.Instance log)
	{
		try
		{
			PlayerManager pm = GameManager.GetPlayerManagerComponent();
			if (pm == null)
			{
				log.Error("拿不到 PlayerManager，物品可能停在盒子里，请手动确认。");
				return;
			}

			pm.AddItemToPlayerInventory(gi, false, false);
		}
		catch (Exception e)
		{
			log.Error($"退回背包失败（物品可能停在盒子里，请手动确认）：{e.Message}");
		}
	}
}
