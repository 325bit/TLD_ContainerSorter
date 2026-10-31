using System;
using Il2CppTLD.IntBackedUnit;

namespace ContainerSorter;

/// <summary>
/// Item readings shared by both directions: name normalisation, unit counts, per-unit and total weight, condition,
/// weight-ledger checks, destroy and unit restore.
/// <see cref="ContainerTransfer"/> and <see cref="TargetCount"/> must use the same tolerances and the same
/// normalisation here, otherwise "top up to N" and "keep N" keep undoing each other. Every member tolerates
/// half-initialised items, so each one wraps its reads in try.
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

	/// <summary>Unity instance names carry a (Clone) suffix; strip it before comparing kinds.</summary>
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

	/// <summary>Units in this instance: <c>m_Units</c> for a stack, 1 otherwise.</summary>
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
			// Unreadable stack data: treat it as a single unit
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

	/// <summary>Weight of one unit (20 arrows share their weight); the whole item when it does not stack. Use this whenever comparing "which is heavier".</summary>
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
			// No stack data: fall back to the whole item
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

	/// <summary>Total item weight in kg; -1 when unreadable so callers skip the check.</summary>
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

	/// <summary>Verifies that a unit change moved the weight by roughly the expected amount (negative = should get lighter). Skipped without a baseline weight.</summary>
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
			// Best effort: the instance is already gone
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
			// Best effort: the instance is already gone
		}
	}

	/// <summary>ItemWeight (integer-backed) to kilograms.</summary>
	internal static float ToKilograms(ItemWeight weight)
	{
		return weight / ItemWeight.FromKilograms(1f);
	}
}
