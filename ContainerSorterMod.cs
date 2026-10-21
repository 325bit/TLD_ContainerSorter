using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ContainerSorter.ContainerSorterMod), "Container Sorter", "1.5.0", "WorkBuddy")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace ContainerSorter;

/// <summary>
/// 打开容器 → 按整理键（默认 R）→ 背包里与容器重复的物品按容器剩余容量塞进容器。
/// 键位/开关在 选项 → Mod Settings → Container Sorter 里调（按键是「点一下再按想要的键」式重绑定）。
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

			// 1.0 版的设置文件里 Key 是 0（旧枚举 R），换成 KeyCode 后会读成 None：这里纠正并落盘
			if (settings.Key == KeyCode.None)
			{
				settings.Key = KeyCode.R;
				settings.Save();
			}

			LoggerInstance.Msg($"Container Sorter 已加载：整理键 = {settings.Key}"
				+ "（选项 → Mod Settings → Container Sorter 里可重新绑定）");

			// 忽略清单：文件不存在就按模板建一个
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

		// 冷却，避免同一次按键在不同回调里重复触发
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
