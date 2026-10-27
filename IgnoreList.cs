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

	/// <summary>保留（至少一个）：背包留 keepCount 个，其余搬进容器；背包不够也绝不去容器里拿。</summary>
	KeepOne = 2,

	/// <summary>
	/// 目标数量（<c>物品ID*数字</c>）：背包里该物品保持 keepCount 份。
	/// 多了按「保留」的逻辑搬进容器，少了从容器取回来 —— 这是与 <see cref="KeepOne"/> 的唯一区别。
	/// </summary>
	SetCount = 3,
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
/// 条目可以带「ID*数字」后缀：
///   带数字 = 目标数量，背包里保持 N 份 —— 多了搬进容器，少了从容器取回；
///   不带数字 = 老行为，背包至少留 1 个，只往外搬。
/// 分隔符用 * 而不是 #，因为 # 本身就是注释符号，两者混在一起会有歧义。
/// 整行只写分区标记的注释会切换后面的处理方式：
///   #全部无视/Ignore All                → 之后列出的物品永不搬进容器（文件开头默认就是这个模式）
///   #保留（至少一个）/Keep At Least One → 之后列出的物品背包至少留 1 个，其余搬进容器
/// 每次整理前按文件修改时间判断是否需要重读，所以改完存盘、下次按键就生效，不用重启。
/// </summary>
internal static class IgnoreList
{
	internal const string FileName = "ContainerSorterIgnore.txt";

	/// <summary>份数后缀的最大值，防止手滑写出天文数字。</summary>
	private const int MaxKeepCount = 999;

	/// <summary>
	/// 目标数量分隔符。<c>GEAR_X*4</c> 表示背包里保持 4 份（多了搬进容器，少了从容器取回）。
	/// 用 <c>*</c> 而不是 <c>#</c>：<c>#</c> 本身就是注释符号，两者放一起会有歧义
	/// （例如 <c>GEAR_X # 4 个备用</c> 到底是份数还是注释）。
	/// </summary>
	private const char Separator = '*';

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
		"# 目标数量（可选）/ Target count (optional)\r\n" +
		"#   物品ID*数字 → 背包里保持 N 份：多了搬进容器，少了从容器取回 / keep exactly N in the backpack:\r\n" +
		"#                  extras go into the container, a shortfall is taken back out of it\r\n" +
		"#   只写物品ID  → 老行为：背包至少留 1 个，只往外搬 / without a number: keep at least 1, only move out\r\n" +
		"#   例 / e.g.：GEAR_HeavyBandage*4 背包保持 4 个 / keep 4 bandages in the backpack\r\n" +
		"#   注意 / note：分隔符是 *（星号），别用 #，因为 # 是注释符号 / use * (asterisk), # is the comment symbol\r\n" +
		"#\r\n" +
		"# 分区标记（整行注释里只写标记本身，不要跟说明文字）/ Section markers (marker only, no extra text)\r\n" +
		"#   全部无视 / Ignore All ：这一段列出的物品永远不会被搬走 / items here are never moved into containers\r\n" +
		"#   保留（至少一个） / Keep At Least One ：这一段列出的物品背包至少留 1 个，其余搬走 / keep at least 1, move the rest\r\n" +
		"# 第一个标记之前的条目按「全部无视」处理 / Entries before the first marker default to Ignore All\r\n" +
		"# ==================================================================\r\n" +
		"\r\n" +
		"#全部无视/Ignore All\r\n" +
		"# GEAR_Revolver\r\n" +
		"# GEAR_Arrow\r\n" +
		"\r\n" +
		"#保留（至少一个）/Keep At Least One\r\n" +
		"# GEAR_Hacksaw\r\n" +
		"# GEAR_HeavyBandage*4\r\n" +
		"# GEAR_SewingKit\r\n";

	private static readonly Dictionary<string, IgnoreRule> rules = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 「目标数量」规则（<c>物品ID*数字</c>），键是归一化后的 prefab 名。与 <see cref="rules"/> 分开存两份的原因：
	/// 这里要按种类挨个核对背包份数，混进 <c>GEAR_</c> 去前缀的别名会让同一条规则被处理两次。
	/// </summary>
	private static readonly Dictionary<string, int> targets = new(StringComparer.OrdinalIgnoreCase);

	private static string? dirPath;
	private static DateTime lastWriteUtc = DateTime.MinValue;
	private static bool loaded;
	private static int ignoreCount;
	private static int keepCount;

	/// <summary>清单文件的完整路径（Mods\ContainerSorterIgnore.txt）。</summary>
	internal static string FilePath => Path.Combine(DirectoryPath(), FileName);

	internal static int Count => ignoreCount + keepCount;

	/// <summary>「目标数量」规则，只读遍历用（<c>kind → 目标份数</c>）。</summary>
	internal static IReadOnlyDictionary<string, int> Targets => targets;

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
					targets.Clear();
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
			int targetTotal = 0;

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
						log.Msg($"  清单分区切换 → {(mode == IgnoreMode.IgnoreAll ? "全部无视/Ignore All" : $"保留（至少一个）/Keep At Least One，默认 {modeKeep} 个")}");
					}

					continue;
				}

				string entry = ExtractEntryText(raw, log, lineNo);
				if (entry.Length == 0)
				{
					continue;
				}

				foreach (EntryToken token in SplitEntry(entry, log, lineNo))
				{
					// 行内写了 *数字 → 目标数量（双向），覆盖分区默认值；
					// 没写 → 沿用分区的模式（全部无视 / 保留至少一个），保持老行为不变
					bool hasCount = token.KeepCount > 0;
					int keepForEntry = hasCount ? Math.Min(token.KeepCount, MaxKeepCount) : modeKeep;
					IgnoreMode modeForEntry = hasCount ? IgnoreMode.SetCount : mode;
					var rule = new IgnoreRule(modeForEntry, keepForEntry);

					if (hasCount && mode == IgnoreMode.IgnoreAll)
					{
						log.Msg($"  第 {lineNo} 行 {token.Name} 指定目标数量 {keepForEntry} 个，按「目标数量」处理（覆盖所在分区的全部无视）");
					}

					next[token.Name] = rule;
					primaries.Add(token.Name);

					// 允许省略 GEAR_ 前缀：同时塞一个去前缀的别名
					if (token.Name.StartsWith("GEAR_", StringComparison.OrdinalIgnoreCase) && token.Name.Length > 5)
					{
						next[token.Name.Substring(5)] = rule;
					}
				}
			}

			rules.Clear();
			targets.Clear();
			int ignoreTotal = 0;
			int keepTotal = 0;

			foreach (KeyValuePair<string, IgnoreRule> kv in next)
			{
				rules[kv.Key] = kv.Value;
			}

			// 统计只看用户真正写下的条目，不数 GEAR_ 前缀别名；目标数量单独计数（别名不进 targets）
			foreach (string primary in primaries)
			{
				if (!next.TryGetValue(primary, out IgnoreRule rule))
				{
					continue;
				}

				switch (rule.Mode)
				{
					case IgnoreMode.SetCount:
						targets[primary] = rule.KeepCount;
						targetTotal++;
						break;
					case IgnoreMode.KeepOne:
						keepTotal++;
						break;
					default:
						ignoreTotal++;
						break;
				}
			}

			ignoreCount = ignoreTotal;
			keepCount = keepTotal;
			loaded = true;
			lastWriteUtc = stamp;

			log.Msg($"忽略清单已载入：全部无视 {ignoreTotal} 个，保留（至少一个）{keepTotal} 个"
				+ (targetTotal > 0 ? $"，目标数量（ID*数字）{targetTotal} 个" : string.Empty)
				+ $"（{path}）");
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

			if (tail.Length == 0 || IsAtLeastOnePhrase(tail))
			{
				mode = IgnoreMode.KeepOne;
				return true;
			}

			if (tail.EndsWith("个", StringComparison.Ordinal))
			{
				tail = tail.Substring(0, tail.Length - 1);
			}
		}
		else if (squashed.StartsWith("keep", StringComparison.Ordinal))
		{
			tail = squashed.Substring(4);
			if (IsAtLeastOnePhrase(tail))
			{
				mode = IgnoreMode.KeepOne;
				return true;
			}
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
		keepCount = Math.Min(n, MaxKeepCount);
		return true;
	}

	/// <summary>
	/// 「保留（至少一个）」这类带括号、不带具体数字的写法 —— 表示「默认留 1 个」。
	/// 全角/半角括号、中英写法都认；判定前先去掉括号。
	/// </summary>
	private static bool IsAtLeastOnePhrase(string tail)
	{
		string bare = tail.Trim('（', '）', '(', ')');

		switch (bare)
		{
			case "至少一个":
			case "至少1个":
			case "至少一":
			case "至少1":
			case "atleastone":
			case "atleast1":
				return true;
		}

		return false;
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

	/// <summary>一行拆出来的单个条目：物品名 + 行内指定的目标数量（0 = 没写，按分区默认）。</summary>
	private readonly struct EntryToken
	{
		internal EntryToken(string name, int keepCount)
		{
			Name = name;
			KeepCount = keepCount;
		}

		internal string Name { get; }

		internal int KeepCount { get; }
	}

	/// <summary>
	/// 拆一行里的多个条目（逗号/分号/制表符）。每个条目允许带「*数字」后缀表示目标数量：
	/// <c>GEAR_HeavyBandage*4</c> → 名字 GEAR_HeavyBandage、背包保持 4 份。
	/// 分隔符用 * 不用 #：# 是注释符号，放一起会有歧义。
	/// </summary>
	private static List<EntryToken> SplitEntry(string line, MelonLogger.Instance log, int lineNo)
	{
		// 容忍从别处粘贴过来的引号
		if (line.Length >= 2 && ((line[0] == '"' && line[^1] == '"') || (line[0] == '\'' && line[^1] == '\'')))
		{
			line = line.Substring(1, line.Length - 2).Trim();
		}

		var result = new List<EntryToken>();
		if (line.Length == 0)
		{
			return result;
		}

		foreach (string part in line.Split(new[] { ',', '，', ';', '；', '\t' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string text = part.Trim();
			if (text.Length == 0)
			{
				continue;
			}

			int keepCount = 0;
			int star = IndexOfSeparator(text);
			if (star >= 0)
			{
				string head = text.Substring(0, star).Trim();
				keepCount = ParseCountSuffix(text, star, out int digitsEnd);

				if (keepCount > 0)
				{
					if (digitsEnd < text.Length)
					{
						// *数字 后面还有字（比如 "GEAR_X*4 备用"），多余的当备注忽略
						log.Msg($"  忽略清单第 {lineNo} 行 {head} 后面还有内容，已忽略：{text.Substring(digitsEnd).Trim()}");
					}
				}
				else
				{
					// * 后面不是数字（或写了 0）—— 整段当备注丢掉，避免把 "GEAR_X* 备注" 当成物品名
					log.Warning($"忽略清单第 {lineNo} 行 {head} 的 {Separator} 后面不是有效份数（需要至少 1 个纯数字），已忽略：{text.Substring(star + 1).Trim()}");
				}

				text = head;
			}

			string one = NormalizeName(text);
			if (one.Length == 0)
			{
				continue;
			}

			if (one.IndexOf(' ') >= 0)
			{
				log.Warning($"忽略清单第 {lineNo} 行有无法识别的空格，已按原样处理：{one}");
			}

			if (keepCount > MaxKeepCount)
			{
				log.Warning($"忽略清单第 {lineNo} 行的目标数量 {keepCount} 太大，已按 {MaxKeepCount} 处理：{one}");
				keepCount = MaxKeepCount;
			}

			result.Add(new EntryToken(one, keepCount));
		}

		return result;
	}

	private static bool IsComment(string line)
	{
		return line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal);
	}

	/// <summary>
	/// 去掉行尾注释（<c>//</c> 或 <c>#</c>）。份数后缀用的是 <c>*</c>，不受影响，
	/// 所以 <c>GEAR_X*4</c> 不会被当成注释。若见到 <c>#数字</c> 这种旧写法，提示改用 <c>*</c>。
	/// </summary>
	private static string ExtractEntryText(string raw, MelonLogger.Instance log, int lineNo)
	{
		string line = raw.Trim();
		if (line.Length == 0 || IsComment(line))
		{
			return string.Empty;
		}

		int slash = line.IndexOf("//", StringComparison.Ordinal);
		int hash = line.IndexOf('#');
		int cut = -1;

		if (slash >= 0)
		{
			cut = slash;
		}

		if (hash >= 0 && (cut < 0 || hash < cut))
		{
			cut = hash;

			// 旧写法 ID#4 里的 # 现在会被当成注释，这里给一句提示，免得悄悄退化成「留 1 个、只出不进」。
			// 只在 # 紧跟数字（老写法）时提示，`GEAR_X # 4 个备用` 这种正常注释不吵。
			int k = hash + 1;
			if (k < line.Length && char.IsDigit(line[k]))
			{
				log.Warning($"忽略清单第 {lineNo} 行的 {Separator} 才是目标数量分隔符，# 是注释符号：{line.Substring(0, hash).Trim()}{Separator}{line.Substring(k)} 会被当成注释，"
					+ $"该项按分区默认处理");
			}
		}

		return cut < 0 ? line.Trim() : line.Substring(0, cut).Trim();
	}

	/// <summary>份数分隔符的位置；全角 ＊（中文输入法下容易打出来）一并当分隔符。</summary>
	private static int IndexOfSeparator(string text)
	{
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] == Separator || text[i] == '＊')
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// 从 <paramref name="starIndex"/> 处的 <c>*</c> 读份数。合法时返回份数并给出数字后的下标，否则返回 0。
	/// </summary>
	private static int ParseCountSuffix(string text, int starIndex, out int digitsEnd)
	{
		digitsEnd = starIndex;

		int i = starIndex + 1;
		while (i < text.Length && (text[i] == ' ' || text[i] == '\u3000'))
		{
			i++;
		}

		int start = i;
		while (i < text.Length && char.IsDigit(text[i]))
		{
			i++;
		}

		if (i == start)
		{
			return 0;
		}

		if (i < text.Length)
		{
			char next = text[i];
			if (!(char.IsWhiteSpace(next) || next == ',' || next == '，' || next == ';' || next == '；' || next == '\t'))
			{
				return 0; // 例如 "*4张" 这种明显不是份数
			}
		}

		digitsEnd = i;
		return int.TryParse(text.Substring(start, i - start), out int value) ? value : 0;
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
