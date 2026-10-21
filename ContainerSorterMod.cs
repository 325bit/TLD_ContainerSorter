using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ContainerSorter.ContainerSorterMod), "Container Sorter", "1.0.0", "WorkBuddy")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace ContainerSorter;

/// <summary>
/// 打开容器 → 按整理键（默认 R）→ 背包里与容器重复的物品按容器剩余容量塞进容器。
/// 键位/开关在 选项 → Mod Settings → Container Sorter 里调。
/// </summary>
public class ContainerSorterMod : MelonMod
{
	internal static Settings? settings;

	/// <summary>可绑定键，顺序必须与 <see cref="SortKey"/> 枚举一致。</summary>
	private static readonly KeyCode[] KeyOrder =
	{
		KeyCode.R, KeyCode.T, KeyCode.F, KeyCode.G, KeyCode.H,
		KeyCode.J, KeyCode.K, KeyCode.L, KeyCode.N, KeyCode.M,
		KeyCode.B, KeyCode.V, KeyCode.C, KeyCode.X, KeyCode.Z
	};

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
			LoggerInstance.Msg("Container Sorter 已加载：打开容器后按 R（可在 选项 → Mod Settings → Container Sorter 改）");
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

		// 冷却，避免同一次按键在不同回调里重复触发
		if (Time.unscaledTime - lastRunTime < CooldownSeconds)
		{
			return;
		}

		bool pressed;
		try
		{
			pressed = Pressed(settings.Key);
		}
		catch (Exception ex)
		{
			// 极端情况：游戏切到新版 Input System 独占时 legacy Input 会抛异常，停用热键而不是每帧刷屏
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
			// 只在前几次和每 200 次时打日志，防止每帧刷屏
			errorCount++;
			if (errorCount <= 3 || errorCount % 200 == 0)
			{
				LoggerInstance.Error($"整理过程异常（第 {errorCount} 次）：{ex}");
			}
		}
	}

	private static bool Pressed(SortKey key)
	{
		int index = (int)key;
		if (index < 0 || index >= KeyOrder.Length)
		{
			index = 0; // 枚举被人为改成越界值时退回 R
		}

		if (settings!.RequireCtrl)
		{
			bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
			if (!ctrl)
			{
				return false;
			}
		}

		return Input.GetKeyDown(KeyOrder[index]);
	}
}
