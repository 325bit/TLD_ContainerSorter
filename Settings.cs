using ModSettings;

namespace ContainerSorter;

/// <summary>可选快捷键。顺序必须与 <see cref="ContainerSorterMod.KeyOrder"/> 一致。</summary>
public enum SortKey
{
	R, T, F, G, H, J, K, L, N, M, B, V, C, X, Z
}

internal class Settings : JsonModSettings
{
	[Section("快捷键")]

	[Name("整理键")]
	[Description("容器打开时按这个键：把背包里「容器中也有的那种物品」按容器剩余重量塞进容器。默认 R")]
	[Choice("R", "T", "F", "G", "H", "J", "K", "L", "N", "M", "B", "V", "C", "X", "Z")]
	public SortKey Key = SortKey.R;

	[Name("需要同时按住 Ctrl")]
	[Description("开启后要 Ctrl + 整理键 才触发，避免和其它 mod / 游戏按键打架。")]
	public bool RequireCtrl = false;

	[Section("行为")]

	[Name("跳过穿戴中/手持物品")]
	[Description("穿在身上的衣物、手上拿的物品不迁移（强烈建议保持开启，否则等于强制脱装备）。")]
	public bool SkipEquipped = true;

	[Name("跳过小背包里的物品")]
	[Description("放在随身小包（satchel）里的物品不迁移，免得把包内层级搞乱。")]
	public bool SkipSatchel = true;

	[Name("输出详细日志")]
	[Description("每次触发都把逐件迁移明细写进 MelonLoader 日志，排查问题时打开。")]
	public bool Verbose = false;
}
