using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ContainerSorter.ContainerSorterMod), "Container Sorter", "1.11.0", "Hemalurgic")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace ContainerSorter;

/// <summary>
/// Sorts the backpack into the open container on the sort key: duplicate kinds move in while capacity allows,
/// and kinds listed as <c>ID*count</c> first get topped up or trimmed back to that count.
/// Key and toggles live under Options → Mod Settings → Container Sorter; the key field is a rebind control.
/// </summary>
public class ContainerSorterMod : MelonMod
{
	internal static Settings? settings;

	private const float CooldownSeconds = 0.25f;

	private float lastRunTime = -99f;
	private int errorCount;
	private bool inputUnavailable;

	public override void OnInitializeMelon()
	{
		try
		{
			settings = new Settings();
			settings.AddToModSettings("Container Sorter");

			// KeyCode 0 deserialises to None even when the stored file meant R; normalise once and persist it
			if (settings.Key == KeyCode.None)
			{
				settings.Key = KeyCode.R;
				settings.Save();
			}

			LoggerInstance.Msg($"Container Sorter 已加载：整理键 = {settings.Key}"
				+ "（选项 → Mod Settings → Container Sorter 里可重新绑定）");

			// Create the ignore list from the template when the file is missing
			IgnoreList.EnsureFile(LoggerInstance);
		}
		catch (Exception ex)
		{
			LoggerInstance.Error("初始化设置失败: " + ex);
		}
	}

	public override void OnUpdate()
	{
		if (settings == null || inputUnavailable)
		{
			return;
		}

		// Debounce: one press can surface in more than one callback
		if (Time.unscaledTime - lastRunTime < CooldownSeconds)
		{
			return;
		}

		bool pressed;
		try
		{
			pressed = Pressed(settings.EffectiveKey);
		}
		catch (Exception ex)
		{
			// Under an Input System-only setup the legacy Input API throws; disable the hotkey instead of logging every frame
			inputUnavailable = true;
			LoggerInstance.Error("读取键盘失败，热键已停用：" + ex.Message);
			return;
		}

		if (!pressed)
		{
			return;
		}

		lastRunTime = Time.unscaledTime;

		try
		{
			ContainerTransfer.Run(LoggerInstance, settings);
		}
		catch (Exception ex)
		{
			// Log the first few failures and every 200th afterwards; a per-frame exception would flood the log
			errorCount++;
			if (errorCount <= 3 || errorCount % 200 == 0)
			{
				LoggerInstance.Error($"整理过程异常（第 {errorCount} 次）：{ex}");
			}
		}
	}

	private static bool Pressed(KeyCode key)
	{
		if (key == KeyCode.None)
		{
			return false;
		}

		if (settings!.RequireCtrl)
		{
			bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
			if (!ctrl)
			{
				return false;
			}
		}

		return Input.GetKeyDown(key);
	}
}
