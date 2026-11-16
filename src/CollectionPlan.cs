using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ArchiveManager
{
    internal sealed class CollectionItem
    {
        public ArchiveEntry EmployeeFolder { get; private set; }
        public string Material { get; private set; }
        public IList<ArchiveEntry> Candidates { get; private set; }
        public ArchiveEntry Chosen { get; private set; }
        public string Employee { get { return EmployeeFolder.Employee; } }
        public string Department { get { return EmployeeFolder.Department; } }
        public string FileName { get { return Chosen == null ? "" : Chosen.Name; } }
        public string Status
        {
            get
            {
                if (Chosen != null) return "可收集";
                if (Candidates.Count == 0) return "未找到";
                return Candidates.Count == 1 ? "待确认" : Candidates.Count + " 个候选";
            }
        }

        public CollectionItem(ArchiveEntry employeeFolder, string material, IList<ArchiveEntry> candidates, bool exact)
        {
            EmployeeFolder = employeeFolder;
            Material = material;
            Candidates = candidates;
            if (exact && candidates.Count == 1) Chosen = candidates[0];
        }

        public void Choose(ArchiveEntry file)
        {
            if (!Candidates.Contains(file)) throw new ArgumentException("File is not a candidate for this item.");
            Chosen = file;
        }

        public string MatchReason(ArchiveEntry file)
        {
            return CollectionPlan.MatchReason(file, Material);
        }
    }

    internal static class CollectionPlan
    {
        public static List<CollectionItem> Build(ArchiveIndex index, IEnumerable<ArchiveEntry> people, IList<string> materials)
        {
            var folders = people.ToList();
            var selected = new HashSet<string>(folders.Select(folder => folder.EmployeePath), StringComparer.OrdinalIgnoreCase);
            var filesByEmployee = index.Files.Where(file => selected.Contains(file.EmployeePath))
                .ToLookup(file => file.EmployeePath, StringComparer.OrdinalIgnoreCase);
            var rows = new List<CollectionItem>();
            foreach (var folder in folders)
            {
                var files = filesByEmployee[folder.EmployeePath].ToList();
                foreach (string material in materials)
                {
                    var candidates = files.Where(file => Path.GetFileNameWithoutExtension(file.Name)
                            .IndexOf(material, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderByDescending(file => MatchReason(file, material) == StandardName)
                        .ThenByDescending(file => file.Modified)
                        .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
                    bool automatic = candidates.Count == 1 && MatchReason(candidates[0], material) == StandardName;
                    rows.Add(new CollectionItem(folder, material, candidates, automatic));
                }
            }
            return rows;
        }

        private const string StandardName = "标准资料名称";
        private static readonly char[] Separators = "_-—（([【.· ）)]】".ToCharArray();
        private static readonly string[] SupplementMarkers = { "补充", "附件", "附录", "草稿", "模板", "样本", "说明" };
        private static readonly Regex DateOnly = new Regex(@"^\d{4}[-._年]?\d{2}[-._月]?\d{2}日?$");

        public static string MatchReason(ArchiveEntry file, string material)
        {
            string name = Path.GetFileNameWithoutExtension(file.Name);
            int match = name.IndexOf(material, StringComparison.OrdinalIgnoreCase);
            if (match < 0) return "名称未匹配";
            string prefix = name.Substring(0, match);
            string suffix = name.Substring(match + material.Length).Trim().Trim(Separators);
            if (file.Employee.Length > 0 && suffix.StartsWith(file.Employee, StringComparison.OrdinalIgnoreCase))
                suffix = suffix.Substring(file.Employee.Length).Trim().Trim(Separators);
            if (SupplementMarkers.Any(marker => prefix.Contains(marker) || suffix.Contains(marker)))
                return "补充件或附件，需确认";
            if (match == 0 && (name.Length == material.Length || Separators.Contains(name[material.Length]) ||
                char.IsWhiteSpace(name[material.Length])) && (suffix.Length == 0 || DateOnly.IsMatch(suffix)))
                return StandardName;
            return "名称含关键词，需确认";
        }
    }
}
