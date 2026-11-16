using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ArchiveManager
{
    internal sealed class MaterialKeywordSuggestion
    {
        public string Keyword { get; private set; }
        public string Example { get; private set; }
        public int Count { get; private set; }
        public bool Checked { get; set; }
        public bool IsAdded { get; set; }
        public bool IsIgnored { get; set; }
        public bool CanAdd { get { return !IsAdded; } }
        public string Status { get { return IsAdded ? "已在关键词中" : IsIgnored ? "已忽略 · 可重加" : "待确认"; } }
        public List<ArchiveEntry> Files { get; set; }
        public string SourcePath { get { return Files == null || Files.Count == 0 ? "" : Files[0].Path; } }
        public string Basis { get { return "根据文件名提取，按关键词忽略大小写分组。请核对姓名、限定词及文件用途。"; } }

        public MaterialKeywordSuggestion(string keyword, string example, int count)
        {
            Keyword = keyword;
            Example = example;
            Count = count;
        }
    }

    internal sealed class MaterialKeywordCatalog
    {
        private static readonly string[] Presets =
        {
            "入职协议", "劳动合同", "入职登记表", "保密协议", "转正审批表", "岗位职责确认书",
            "岗位调整通知书", "培训签到记录", "绩效考核记录", "考勤异常说明", "档案移交清单", "入职材料签收单"
        };
        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
        private static readonly char[] Separators = "_＿-－—·. ".ToCharArray();
        private static readonly Regex LeadingMetadata = new Regex(
            @"^(?:[12][0-9]{3}[-._年]?[01][0-9][\-._月]?[0-3][0-9]日?|[A-Z]{1,6}[0-9]{3,}|[0-9]+)[\s_＿\-－—·.]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex TrailingMetadata = new Regex(
            @"(?:[\s_＿\-－—·.]+(?:[12][0-9]{3}[-._年]?[01][0-9][\-._月]?[0-3][0-9]日?|[A-Z]{1,6}[0-9]{3,}|v[0-9]+(?:\.[0-9]+)*|[0-9]{3,})|[（(][0-9]+[）)])$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex IdentifierOnly = new Regex(@"^[A-Z]{1,6}[0-9]{3,}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SuffixNote = new Regex(
            @"[（(\[【](?:扫描件|复印件|签署版|电子版|盖章版)[）)\]】]$");
        private static readonly Regex Qualifier = new Regex(@"作废|撤销|补充|附件|草稿|取消|修订|void|draft|supplement",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly string[] Variants = { "补充", "附件", "扫描件", "复印件", "签署版", "盖章版" };
        private static readonly HashSet<string> GenericNames = new HashSet<string>(new[]
        {
            "文件", "文档", "资料", "材料", "扫描件", "复印件", "图片", "附件", "证明", "未命名", "新建文档",
            "scan", "document", "file", "image", "img", "untitled", "copy"
        }, Comparer);
        private static readonly HashSet<string> NonMaterialExtensions = new HashSet<string>(new[]
        {
            ".tmp", ".bak", ".lnk", ".exe", ".dll", ".ini", ".db", ".sys"
        }, Comparer);

        private readonly string path;
        // true means added; false means the user chose to ignore the suggestion.
        private Dictionary<string, bool> saved;

        public MaterialKeywordCatalog(string path = null)
        {
            this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PersonnelArchiveManager", "material-keywords.txt");
            saved = new Dictionary<string, bool>(Comparer);
        }

        public void Load()
        {
            saved = Read();
        }

        public bool IsPreset(string value) { return Presets.Contains(value, Comparer); }

        public void UpdateStates(IEnumerable<MaterialKeywordSuggestion> suggestions)
        {
            var known = new HashSet<string>(Keywords, Comparer);
            foreach (var item in suggestions)
            {
                item.IsAdded = known.Contains(item.Keyword);
                item.IsIgnored = saved.ContainsKey(item.Keyword) && !saved[item.Keyword];
            }
        }

        public string[] Keywords
        {
            get { return Presets.Concat(saved.Where(entry => entry.Value).Select(entry => entry.Key)
                .OrderBy(word => word, StringComparer.CurrentCultureIgnoreCase)).ToArray(); }
        }

        public static bool IsValid(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                !value.Any(char.IsControl) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        public void SaveChanges(IEnumerable<string> add, IEnumerable<string> ignore)
        {
            var next = Read();
            foreach (var word in ignore.Select(w => w.Trim()))
                if (IsValid(word) && !IsPreset(word)) next[word] = false;
            foreach (var word in add.Select(w => w.Trim()))
                if (IsValid(word) && !IsPreset(word)) next[word] = true;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temporary, next.OrderBy(pair => pair.Key, Comparer)
                    .Select(pair => (pair.Value ? "added\t" : "ignored\t") + pair.Key));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                saved = next;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private Dictionary<string, bool> Read()
        {
            var result = new Dictionary<string, bool>(Comparer);
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadLines(path))
            {
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                string flag = line.Substring(0, tab);
                string word = line.Substring(tab + 1);
                if ((flag == "added" || flag == "ignored") && IsValid(word) && !IsPreset(word))
                    result[word] = flag == "added";
            }
            return result;
        }

        public List<MaterialKeywordSuggestion> Discover(IEnumerable<ArchiveEntry> files, bool includeExisting = false,
            CancellationToken cancellation = default(CancellationToken))
        {
            string[] known = Keywords;
            var excluded = new HashSet<string>(known.Concat(saved.Keys), Comparer);
            return files.Where(file => !file.IsFolder && !file.Name.StartsWith("~$", StringComparison.Ordinal) &&
                    !NonMaterialExtensions.Contains(Path.GetExtension(file.Name)))
                .Select(file =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return new { Keyword = InferName(file), File = file };
                })
                .Where(item => item.Keyword != null && (includeExisting || !excluded.Contains(item.Keyword)))
                .Where(item => includeExisting || !known.Any(word => item.Keyword.StartsWith(word, StringComparison.OrdinalIgnoreCase) &&
                    Variants.Any(variant => item.Keyword.Substring(word.Length).Trim(Separators).StartsWith(variant, StringComparison.Ordinal))))
                .GroupBy(item => item.Keyword, Comparer)
                .Select(group => new MaterialKeywordSuggestion(group.Key, group.First().File.Name, group.Count())
                    { IsAdded = known.Contains(group.Key, Comparer),
                        IsIgnored = saved.ContainsKey(group.Key) && !saved[group.Key],
                        Files = group.Select(item => item.File).ToList() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Keyword, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string InferName(ArchiveEntry file)
        {
            var identity = new[] { file.Employee, file.Department, Path.GetFileName(file.Parent.TrimEnd('\\', '/')) }
                .SelectMany(value => new[] { value }.Concat(value.Split(Separators, StringSplitOptions.RemoveEmptyEntries)))
                .Where(value => value.Length > 0)
                .Distinct(Comparer).OrderByDescending(value => value.Length).ToList();
            string title = Path.GetFileNameWithoutExtension(file.Name);
            string previous;
            do
            {
                previous = title;
                title = title.Trim().Trim(Separators);
                title = LeadingMetadata.Replace(title, "");
                title = TrailingMetadata.Replace(title, "");
                title = SuffixNote.Replace(title, "");
                foreach (var token in identity)
                {
                    if (title.StartsWith(token, StringComparison.OrdinalIgnoreCase) &&
                        title.Length > token.Length && IsSeparator(title[token.Length]))
                        title = title.Substring(token.Length).TrimStart(Separators).TrimStart();
                    if (title.EndsWith(token, StringComparison.OrdinalIgnoreCase) &&
                        title.Length > token.Length && IsSeparator(title[title.Length - token.Length - 1]))
                        title = title.Substring(0, title.Length - token.Length).TrimEnd(Separators).TrimEnd();
                }
            } while (title != previous);

            // Recognize document-type phrases even in a flat folder with no employee hierarchy.
            var phrases = title.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.Length >= 3 && Regex.IsMatch(part,
                    @"(?:协议|合同|报告|证明|证书|登记表|申请表|审批表|确认书|承诺书|通知书|交接单|清单|记录|说明)$"))
                .Distinct(Comparer).ToList();
            if (phrases.Count == 1 && !Qualifier.IsMatch(title)) title = phrases[0];

            return IsValid(title) && title.Any(char.IsLetter) && title.Count(char.IsLetterOrDigit) >= 2 &&
                !IdentifierOnly.IsMatch(title) &&
                !GenericNames.Contains(title) ? title : null;
        }

        private static bool IsSeparator(char ch)
        {
            return char.IsWhiteSpace(ch) || Array.IndexOf(Separators, ch) >= 0;
        }
    }
}
