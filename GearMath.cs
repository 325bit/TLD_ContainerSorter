using System;
using Il2CppTLD.IntBackedUnit;

namespace ContainerSorter;

/// <summary>
/// 两个方向共用的物品读数：名字归一化、份数、单份/整堆重量、耐久、重量账本校验、销毁与份数还原。
/// 抽出来是为了让 <see cref="ContainerTransfer"/>（背包 → 容器）与 <see cref="TargetCount"/>（容器 → 背包）
/// 走同一套口径 —— 容差或归一化只要有一边走偏，「补到 N 份」和「留下 N 份」就会互相打架。
/// 每个方法都会碰到读不到成员的物品（被别的 mod 改残、实例化到一半），所以一律 try 兜住。
/// </summary>
internal static class GearMath
{
	internal static string? SafeName(GearItem gi)
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

	/// <summary>Unity 实例名会带 (Clone) 后缀，统一去掉再比较。</summary>
	internal static string NormalizeName(string name)
	{
		string n = name.Trim();
		if (n.EndsWith("(Clone)", StringComparison.Ordinal))
		{
			n = n.Substring(0, n.Length - "(Clone)".Length).Trim();
		}

		return n;
	}

	internal static int InstanceId(GearItem gi)
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
	internal static int UnitsOf(GearItem gi)
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

	internal static float ItemKilograms(GearItem gi)
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

	/// <summary>单份重量：一堆 20 支箭按每支摊开，非堆叠就是整件重量。比较「谁更重」用这个。</summary>
	internal static float UnitKilograms(GearItem gi)
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

	internal static float Condition(GearItem gi)
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

	/// <summary>物品总重量（kg）；读不到返回 -1，让校验自动跳过。</summary>
	internal static float SafeWeight(GearItem gi)
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

	/// <summary>数量改了以后核对重量变化是否符合预期（预期为负表示应该变轻）。抓不到基准重量就跳过校验。</summary>
	internal static bool WeightMatches(float before, float after, float expected)
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

	internal static void RestoreUnits(GearItem gi, int units)
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

	internal static void Slay(GearItem gi)
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

	/// <summary>ItemWeight（内部以 long 记账）→ 公斤浮点。</summary>
	internal static float ToKilograms(ItemWeight weight)
	{
		return weight / ItemWeight.FromKilograms(1f);
	}
}
