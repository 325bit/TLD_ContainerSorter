using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;

namespace ContainerSorter;

/// <summary>清单里一条物品 ID 的处理方式。</summary>
internal enum IgnoreMode
{
	/// <summary>不在清单里，正常迁移。</summary>
	None = 0,

	/// <summary>全部无视：一个都不搬。</summary>
	IgnoreAll = 1,

	/// <summary>保留一个：背包留 keepCount 个，其余搬进容器。</summary>
	KeepOne = 2,
}

internal readonly struct IgnoreRule
{
	internal IgnoreRule(IgnoreMode mode, int keepCount)
	{
		Mode = mode;
		KeepCount = keepCount;
	}

	internal IgnoreMode Mode { get; }

	internal int KeepCount { get; }

	internal static IgnoreRule None => new IgnoreRule(IgnoreMode.None, 0);
}

/// <summary>
/// Mods\ContainerSorterIgnore.txt 里的物品规则。
/// 文件按行分隔，每行一个物品 ID（prefab 名）；# 或 // 开头为注释。
/// 整行只写分区标记的注释会切换后面的处理方式：
///   #全部无视/Ignore All  → 之后列出的物品永不搬进容器（文件开头默认就是这个模式）
///   #保留一个/Keep One    → 之后列出的物品背包留 1 个，其余搬进容器
/// 每次整理前按文件修改时间判断是否需要重读，所以改完存盘、下次按键就生效，不用重启。
/// </summary>
internal static class IgnoreList
{
	internal const string FileName = "ContainerSorterIgnore.txt";

	/// <summary>首次运行写出来的模板：全部是注释，等于默认不忽略任何东西。</summary>
	private const string Template =
		"# Container Sorter 忽略清单 / Ignore List\r\n" +
		"# ==================================================================\r\n" +
		"# 每行一个物品 ID（游戏内部 prefab 名）/ One item ID per line (internal prefab name)\r\n" +
		"# 带不带 (Clone) 后缀都行 / The (Clone) suffix is optional\r\n" +
		"# 大小写不敏感，GEAR_ 前缀可省略 / Case-insensitive, the GEAR_ prefix may be omitted\r\n" +
		"# 以 # 或 // 开头的行是注释，行尾也能跟注释 / # or // starts a comment, trailing comments are fine\r\n" +
		"# 改完保存就生效，不用重启游戏 / Save the file, it reloads on the next sort, no restart needed\r\n" +
		"#\r\n" +
		"# 分区标记（整行注释里只写标记本身，不要跟说明文字）/ Section markers (marker only, no extra text)\r\n" +
		"#   全部无视 / Ignore All ：这一段列出的物品永远不会被搬走 / items here are never moved into containers\r\n" +
		"#   保留一个 / Keep One   ：这一段列出的物品背包留 1 个，其余搬走 / keep 1 in the backpack, move the rest\r\n" +
		"# 第一个标记之前的条目按「全部无视」处理 / Entries before the first marker default to Ignore All\r\n" +
		"# ==================================================================\r\n" +
		"\r\n" +
		"#全部无视/Ignore All\r\n" +
		"# GEAR_Revolver\r\n" +
		"# GEAR_Arrow\r\n" +
		"\r\n" +
		"#保留一个/Keep One\r\n" +
		"# GEAR_Hacksaw\r\n" +
		"# GEAR_SewingKit\r\n";

	private static readonly Dictionary<string, IgnoreRule> rules = new(StringComparer.OrdinalIgnoreCase);

	private static string? dirPath;
	private static DateTime lastWriteUtc = DateTime.MinValue;
	private static bool loaded;
	private static int ignoreCount;
	private static int keepCount;

	/// <summary>清单文件的完整路径（Mods\ContainerSorterIgnore.txt）。</summary>
	internal static string FilePath => Path.Combine(DirectoryPath(), FileName);

	internal static int Count => ignoreCount + keepCount;

	/// <summary>启动时调用：文件不存在就按模板创建一个。</summary>
	internal static void EnsureFile(MelonLogger.Instance log)
	{
		try
		{
			string path = FilePath;
			if (!File.Exists(path))
			{
				File.WriteAllText(path, Template, new UTF8Encoding(false));
				log.Msg($"已创建忽略清单模板：{path}");
			}
			else
			{
				log.Msg($"忽略清单：{path}");
			}

			Reload(log, true);
		}
		catch (Exception e)
		{
			log.Warning("处理忽略清单失败：" + e.Message);
		}
	}

	/// <summary>按修改时间增量重读；文件没变就直接返回，代价只有一次 stat。</summary>
	internal static void Reload(MelonLogger.Instance log, bool force = false)
	{
		try
		{
			string path = FilePath;

			if (!File.Exists(path))
			{
				if (loaded || rules.Count > 0)
				{
					rules.Clear();
					loaded = false;
					ignoreCount = 0;
					keepCount = 0;
					lastWriteUtc = DateTime.MinValue;
					log.Warning($"忽略清单已被删除，本次不忽略任何物品：{path}");
				}
				return;
			}

			DateTime stamp = File.GetLastWriteTimeUtc(path);
			if (!force && loaded && stamp == lastWriteUtc)
			{
				return;
			}

			var next = new Dictionary<string, IgnoreRule>(StringComparer.OrdinalIgnoreCase);
			var primaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			IgnoreMode mode = IgnoreMode.IgnoreAll; // 第一个标记之前按老行为：全部无视
			int modeKeep = 1;
			int lineNo = 0;

			foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
			{
				lineNo++;

				string trimmed = raw.Trim();
				if (trimmed.Length == 0)
				{
					continue;
				}

				// 注释行：先看是不是分区标记，否则跳过
				if (IsComment(trimmed))
				{
					string body = trimmed.StartsWith("//", StringComparison.Ordinal)
						? trimmed.Substring(2)
						: trimmed.Substring(1);

					if (TryParseMarker(body, out IgnoreMode markerMode, out int markerKeep))
					{
						mode = markerMode;
						modeKeep = markerKeep;
						log.Msg($"  清单分区切换 → {(mode == IgnoreMode.IgnoreAll ? "全部无视/Ignore All" : $"保留 {modeKeep} 个/Keep {modeKeep}")}");
					}

					continue;
				}

				string entry = StripComment(trimmed);
				var rule = new IgnoreRule(mode, modeKeep);

				foreach (string one in SplitEntry(entry, log, lineNo))
				{
					next[one] = rule;
					primaries.Add(one);

					// 允许省略 GEAR_ 前缀：同时塞一个去前缀的别名
					if (one.StartsWith("GEAR_", StringComparison.OrdinalIgnoreCase) && one.Length > 5)
					{
						next[one.Substring(5)] = rule;
					}
				}
			}

			rules.Clear();
			int ignoreTotal = 0;
			int keepTotal = 0;

			foreach (KeyValuePair<string, IgnoreRule> kv in next)
			{
				rules[kv.Key] = kv.Value;
			}

			// 统计只看用户真正写下的条目，不数 GEAR_ 前缀别名
			foreach (string primary in primaries)
			{
				if (next.TryGetValue(primary, out IgnoreRule rule) && rule.Mode == IgnoreMode.KeepOne)
				{
					keepTotal++;
				}
				else
				{
					ignoreTotal++;
				}
			}

			ignoreCount = ignoreTotal;
			keepCount = keepTotal;
			loaded = true;
			lastWriteUtc = stamp;

			log.Msg($"忽略清单已载入：全部无视 {ignoreTotal} 个，保留一个 {keepTotal} 个（{path}）");
		}
		catch (Exception e)
		{
			log.Warning("读取忽略清单失败，本次不忽略任何物品：" + e.Message);
		}
	}

	/// <summary>kind 需先按 <see cref="NormalizeName"/> 归一化（去 (Clone)）。不在清单里返回 Mode = None。</summary>
	internal static IgnoreRule Lookup(string kind)
	{
		if (rules.Count == 0 || string.IsNullOrEmpty(kind))
		{
			return IgnoreRule.None;
		}

		return rules.TryGetValue(kind, out IgnoreRule rule) ? rule : IgnoreRule.None;
	}

	// ---------------------------------------------------------------- 解析

	/// <summary>
	/// 识别分区标记。只接受「整行注释里只写标记本身」的形式，
	/// 所以模板里那些带说明文字的示例行（含 —— 或括号说明）不会被误当成标记。
	/// </summary>
	private static bool TryParseMarker(string body, out IgnoreMode mode, out int keepCount)
	{
		mode = IgnoreMode.None;
		keepCount = 1;

		string[] parts = body.Split(new[] { '/', '／', '|', '｜' }, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0 || parts.Length > 2)
		{
			return false;
		}

		IgnoreMode found = IgnoreMode.None;
		int count = 1;

		foreach (string part in parts)
		{
			if (!TryMatchKeyword(Squash(part), out IgnoreMode partMode, out int partCount))
			{
				return false; // 任何一段不像标记 → 整行按普通注释处理
			}

			if (found != IgnoreMode.None && found != partMode)
			{
				return false; // 中英两段互相矛盾
			}

			found = partMode;
			if (partCount > count)
			{
				count = partCount;
			}
		}

		if (found == IgnoreMode.None)
		{
			return false;
		}

		mode = found;
		keepCount = found == IgnoreMode.KeepOne ? count : 1;
		return true;
	}

	/// <summary>squashed 已去掉所有空白并转小写。</summary>
	private static bool TryMatchKeyword(string squashed, out IgnoreMode mode, out int keepCount)
	{
		mode = IgnoreMode.None;
		keepCount = 1;

		switch (squashed)
		{
			case "全部无视":
			case "无视全部":
			case "忽略全部":
			case "ignoreall":
				mode = IgnoreMode.IgnoreAll;
				return true;
		}

		string? tail;

		if (squashed.StartsWith("保留", StringComparison.Ordinal))
		{
			tail = squashed.Substring(2);
			if (tail.EndsWith("个", StringComparison.Ordinal))
			{
				tail = tail.Substring(0, tail.Length - 1);
			}

			if (tail.Length == 0)
			{
				mode = IgnoreMode.KeepOne;
				return true;
			}
		}
		else if (squashed.StartsWith("keep", StringComparison.Ordinal))
		{
			tail = squashed.Substring(4);
		}
		else
		{
			return false;
		}

		int n = ParseNumber(tail);
		if (n <= 0)
		{
			return false;
		}

		mode = IgnoreMode.KeepOne;
		keepCount = Math.Min(n, 99);
		return true;
	}

	private static int ParseNumber(string text)
	{
		switch (text)
		{
			case "one":
			case "一":
			case "1":
				return 1;
			case "两":
			case "二":
			case "2":
				return 2;
			case "三":
			case "3":
				return 3;
			case "四":
			case "4":
				return 4;
			case "五":
			case "5":
				return 5;
			case "六":
			case "6":
				return 6;
			case "七":
			case "7":
				return 7;
			case "八":
			case "8":
				return 8;
			case "九":
			case "9":
				return 9;
			case "十":
			case "10":
				return 10;
		}

		return int.TryParse(text, out int value) ? value : -1;
	}

	/// <summary>去空白 + 小写，标记判定用。</summary>
	private static string Squash(string text)
	{
		var sb = new StringBuilder(text.Length);

		foreach (char c in text)
		{
			if (char.IsWhiteSpace(c) || c == '\u3000')
			{
				continue;
			}

			sb.Append(char.ToLowerInvariant(c));
		}

		return sb.ToString();
	}

	private static IEnumerable<string> SplitEntry(string line, MelonLogger.Instance log, int lineNo)
	{
		// 容忍从别处粘贴过来的引号
		if (line.Length >= 2 && ((line[0] == '"' && line[^1] == '"') || (line[0] == '\'' && line[^1] == '\'')))
		{
			line = line.Substring(1, line.Length - 2).Trim();
		}

		if (line.Length == 0)
		{
			return Array.Empty<string>();
		}

		var result = new List<string>();

		// 一行里用逗号/分号/制表符分隔多个也算合法
		foreach (string part in line.Split(new[] { ',', '，', ';', '；', '\t' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string one = NormalizeName(part);
			if (one.Length == 0)
			{
				continue;
			}

			if (one.IndexOf(' ') >= 0)
			{
				log.Warning($"忽略清单第 {lineNo} 行有无法识别的空格，已按原样处理：{one}");
			}

			result.Add(one);
		}

		return result;
	}

	private static bool IsComment(string line)
	{
		return line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal);
	}

	private static string StripComment(string raw)
	{
		string line = raw.Trim();
		if (line.Length == 0 || IsComment(line))
		{
			return string.Empty;
		}

		int hash = line.IndexOf('#');
		if (hash >= 0)
		{
			line = line.Substring(0, hash).Trim();
		}

		int slash = line.IndexOf("//", StringComparison.Ordinal);
		if (slash >= 0)
		{
			line = line.Substring(0, slash).Trim();
		}

		return line;
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

	// ---------------------------------------------------------------- 目录

	private static string DirectoryPath()
	{
		if (!string.IsNullOrEmpty(dirPath))
		{
			return dirPath!;
		}

		try
		{
			string? mods = MelonLoader.Utils.MelonEnvironment.ModsDirectory;
			if (!string.IsNullOrEmpty(mods))
			{
				dirPath = mods;
				return dirPath!;
			}
		}
		catch
		{
			// 老版本 MelonLoader 没有这个字段，走下面的兜底
		}

		try
		{
			// 本 dll 就装在 Mods 目录里，取它自己所在的文件夹
			dirPath = Path.GetDirectoryName(typeof(IgnoreList).Assembly.Location);
		}
		catch
		{
			dirPath = null;
		}

		if (string.IsNullOrEmpty(dirPath))
		{
			dirPath = ".";
		}

		return dirPath!;
	}
}
