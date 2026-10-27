using ModSettings;
using UnityEngine;

namespace ContainerSorter;

internal class Settings : JsonModSettings
{
	[Section("快捷键")]

	[Name("整理键")]
	[Description("容器打开时按这个键：把背包里「容器中也有的那种物品」按容器剩余重量塞进容器。点一下按钮再按下想要的键即可重新绑定。")]
	public KeyCode Key = KeyCode.R;

	[Name("需要同时按住 Ctrl")]
	[Description("开启后要 Ctrl + 整理键 才触发，避免和其它 mod / 游戏按键打架。")]
	public bool RequireCtrl = false;

	[Section("行为")]

	[Name("合并可堆叠物品")]
	[Description("搬进去的木柴/火柴/箭等，如果容器里已有同款、状态接近的堆叠，直接并进那一堆，而不是新开一堆。")]
	public bool MergeStacks = true;

	[Name("跳过穿戴中/手持物品")]
	[Description("穿在身上的衣物、手上拿的物品不迁移（强烈建议保持开启，否则等于强制脱装备）。")]
	public bool SkipEquipped = true;

	[Name("跳过小背包里的物品")]
	[Description("放在随身小包（satchel）里的物品不迁移，免得把包内层级搞乱。")]
	public bool SkipSatchel = true;

	[Name("启用忽略清单")]
	[Description("开启后读取 Mods\\ContainerSorterIgnore.txt：「#全部无视」段里的物品不搬，「#保留（至少一个）」段里的物品背包留 N 个（默认 1）、其余搬进容器；写了 ID*数字 的物品是「目标数量」，背包多了搬进容器、少了从容器取回。改完文件存盘即生效，无需重启。")]
	public bool UseIgnoreList = true;

	[Name("保留时优先把高耐久放进容器")]
	[Description("「保留（至少一个）」/「目标数量」遇到同种物品耐久不同时，背包留耐久最低的那件，状态好的收进容器。关掉则反过来：背包留最好的。")]
	public bool KeepBestInContainer = true;

	[Name("耐久相同时优先搬更重的")]
	[Description("「保留（至少一个）」/「目标数量」遇到同种物品耐久一样、重量不同时，背包留单份更轻的那个，重的搬进容器（主要针对肉类：同样新鲜度下先搬 1kg 的肉块，半公斤的留在背包）。容器快满时也按这个顺序先塞重的。")]
	public bool HeavierFirstWhenEqualCondition = true;

	[Name("输出详细日志")]
	[Description("每次触发都把逐件迁移明细写进 MelonLoader 日志，排查问题时打开。")]
	public bool Verbose = false;

	/// <summary>旧版 json 里存的是 SortKey 的 0，读进来会变成 KeyCode.None（等于没绑键）。</summary>
	internal KeyCode EffectiveKey
	{
		get
		{
			if (Key == KeyCode.None)
			{
				Key = KeyCode.R;
			}

			return Key;
		}
	}
}
