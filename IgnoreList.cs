using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;

namespace ContainerSorter;

/// <summary>How one item ID from the list is handled.</summary>
internal enum IgnoreMode
{
	/// <summary>Not listed: move normally.</summary>
	None = 0,

	/// <summary>Ignore all: never move it.</summary>
	IgnoreAll = 1,

	/// <summary>Keep at least N: leave N units in the backpack and move the rest in, but never take any back out.</summary>
	KeepOne = 2,

	/// <summary>
	/// Target count (<c>ID*count</c>): keep exactly N units in the backpack.
	/// A surplus moves in like <see cref="KeepOne"/>; a shortfall is taken back out — the only difference between the two modes.
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
/// Item rules read from Mods\ContainerSorterIgnore.txt: one item ID (prefab name) per line, # or // starts a comment.
/// A trailing <c>*N</c> means a target count, a bare ID means "keep at least one, only move outward".
/// Comments that contain nothing but a section marker switch the mode for the lines below them
/// (Ignore All / Keep At Least One); entries before the first marker default to Ignore All.
/// The file is re-read when its timestamp changes, so a saved edit applies on the next key press.
/// Parsing rules and pitfalls: docs/DEVELOPMENT.md §7.
/// </summary>
internal static class IgnoreList
{
	internal const string FileName = "ContainerSorterIgnore.txt";

	/// <summary>Upper bound for a count suffix, so a typo cannot ask for a million units.</summary>
	private const int MaxKeepCount = 999;

	/// <summary>
	/// Target-count separator: <c>GEAR_X*4</c> keeps 4 units in the backpack.
	/// It cannot be <c>#</c>, which already starts a comment and makes "count or note?" ambiguous.
	/// </summary>
	private const char Separator = '*';

	/// <summary>First-run template: comments only, so nothing is ignored until the player edits it.</summary>
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
	/// Target-count rules keyed by normalised prefab name. Kept apart from <see cref="rules"/> because these entries are
	/// walked kind by kind, where a <c>GEAR_</c>-less alias would make the same rule run twice.
	/// </summary>
	private static readonly Dictionary<string, int> targets = new(StringComparer.OrdinalIgnoreCase);

	private static string? dirPath;
	private static DateTime lastWriteUtc = DateTime.MinValue;
	private static bool loaded;
	private static int ignoreCount;
	private static int keepCount;

	/// <summary>Full path of the list (Mods\ContainerSorterIgnore.txt).</summary>
	internal static string FilePath => Path.Combine(DirectoryPath(), FileName);

	internal static int Count => ignoreCount + keepCount;

	/// <summary>Target-count rules for read-only iteration (<c>kind → units</c>).</summary>
	internal static IReadOnlyDictionary<string, int> Targets => targets;

	/// <summary>Called on startup: writes the template when the file does not exist yet.</summary>
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

	/// <summary>Reloads only when the timestamp changed, so a run that changes nothing costs one stat call.</summary>
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
			IgnoreMode mode = IgnoreMode.IgnoreAll; // Before the first marker, entries default to Ignore All
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

				// Comment line: a marker switches the section mode, anything else is skipped
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
					// An inline *N makes it a target count (both directions) and overrides the section default;
					// without a number the entry inherits the section mode
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

					// The GEAR_ prefix is optional: also store the stripped alias
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

			// Count only the entries the player actually wrote, never the aliases; target counts live in their own table
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

	/// <summary><c>kind</c> must already be normalised (no (Clone)). Returns Mode = None when the kind is not listed.</summary>
	internal static IgnoreRule Lookup(string kind)
	{
		if (rules.Count == 0 || string.IsNullOrEmpty(kind))
		{
			return IgnoreRule.None;
		}

		return rules.TryGetValue(kind, out IgnoreRule rule) ? rule : IgnoreRule.None;
	}

	// ---------------------------------------------------------------- Parsing

	/// <summary>
	/// Recognises a section marker, but only when the whole comment is the marker itself, so the annotated example lines
	/// in the template never switch modes.
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
				return false; // One unexplained segment: treat the whole line as a plain comment
			}

			if (found != IgnoreMode.None && found != partMode)
			{
				return false; // The two halves disagree
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

	/// <summary><paramref name="squashed"/> has no whitespace left and is lower case.</summary>
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
	/// Matches the "at least one" phrasing, i.e. a keep marker with no explicit number, in either language and either
	/// bracket width; brackets are stripped before matching.
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

	/// <summary>Strips whitespace and lower-cases the text; markers are matched against this form.</summary>
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

	/// <summary>One entry from a line: item name plus the inline target count (0 = none, use the section default).</summary>
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
	/// Splits a line into entries on comma, semicolon or tab. A trailing <c>*N</c> sets the target count:
	/// <c>GEAR_HeavyBandage*4</c> keeps 4 units in the backpack. <c>*</c> is used because <c>#</c> starts a comment.
	/// </summary>
	private static List<EntryToken> SplitEntry(string line, MelonLogger.Instance log, int lineNo)
	{
		// Tolerate quotes pasted in from elsewhere
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
						// Text after the digits ("GEAR_X*4 spare") is treated as a note
						log.Msg($"  忽略清单第 {lineNo} 行 {head} 后面还有内容，已忽略：{text.Substring(digitsEnd).Trim()}");
					}
				}
				else
				{
					// No valid digits after * (or a zero): drop it, otherwise "GEAR_X* note" would join the item name
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
	/// Strips a trailing comment (<c>//</c> or <c>#</c>). The count suffix uses <c>*</c>, so <c>GEAR_X*4</c> survives;
	/// a <c>#N</c> form is reported once instead of silently collapsing into "keep one, move out only".
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

			// A # followed by digits is the retired count syntax and now starts a comment, so say so; an ordinary
			// trailing note such as "GEAR_X # 4 spare" stays quiet.
			int k = hash + 1;
			if (k < line.Length && char.IsDigit(line[k]))
			{
				log.Warning($"忽略清单第 {lineNo} 行的 {Separator} 才是目标数量分隔符，# 是注释符号：{line.Substring(0, hash).Trim()}{Separator}{line.Substring(k)} 会被当成注释，"
					+ $"该项按分区默认处理");
			}
		}

		return cut < 0 ? line.Trim() : line.Substring(0, cut).Trim();
	}

	/// <summary>Index of the count separator, accepting the full-width variant that IMEs produce.</summary>
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
	/// Reads the count that starts at <paramref name="starIndex"/>, reporting the index just past the digits.
	/// Returns 0 when what follows is not a plain number.
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

	/// <summary>Unity instance names carry a (Clone) suffix; strip it before comparing kinds.</summary>
	private static string NormalizeName(string name)
	{
		string n = name.Trim();
		if (n.EndsWith("(Clone)", StringComparison.Ordinal))
		{
			n = n.Substring(0, n.Length - "(Clone)".Length).Trim();
		}

		return n;
	}

	// ---------------------------------------------------------------- Directory

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
			// Older loaders do not expose this field; fall through to the assembly location
		}

		try
		{
			// The dll lives in Mods, so its own folder is the answer
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
