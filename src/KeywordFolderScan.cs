using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ArchiveManager
{
    // Retains unconfirmed results for this application session only.
    internal sealed class KeywordFolderScan
    {
        public string Folder { get; private set; }
        public ArchiveIndex Index { get; private set; }
        public List<MaterialKeywordSuggestion> Suggestions { get; set; }

        public void Apply(KeywordFolderScan result)
        {
            Folder = result.Folder;
            Index = result.Index;
            Suggestions = result.Suggestions;
        }

        public void Scan(string folder, Action<int> progress, CancellationToken cancellation)
        {
            var scanned = ArchiveIndex.Scan(new ArchiveSettings { Root = folder,
                DepartmentLevel = int.MaxValue, EmployeeLevel = int.MaxValue }, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Folder = folder;
            Index = scanned;
        }

        public List<ArchiveEntry> Unrecognized(IList<MaterialKeywordSuggestion> suggestions)
        {
            var recognized = new HashSet<string>(suggestions.SelectMany(item => item.Files)
                .Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
            return Index.Files.Where(file => !recognized.Contains(file.Path)).ToList();
        }
    }
}
