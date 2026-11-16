using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace ArchiveManager
{
    internal static class ReleaseAudit
    {
        private static int passed;

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
            passed++;
        }

        private static string Hash(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
                return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        private static ArchiveIndex Scan(string root)
        {
            return ArchiveIndex.Scan(new ArchiveSettings { Root = root }, count => { });
        }

        private static string Write(string parent, string name, string content)
        {
            var path = Path.Combine(parent, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static bool RejectsOutput(string destination, string root)
        {
            try { ArchiveFiles.PrepareOutput(destination, root); return false; }
            catch (IOException error) { return error.Message.Contains("档案库内"); }
        }

        private static void TestRelease(string temp)
        {
            string root = Directory.CreateDirectory(Path.Combine(temp, "Archive")).FullName;
            string first = Directory.CreateDirectory(Path.Combine(root, "财务部", "同名员工_HR0001")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(root, "销售部", "同名员工_HR0001")).FullName;
            string export = Directory.CreateDirectory(Path.Combine(temp, "ArchiveCopies")).FullName;
            string supplement = Write(first, "入职协议（补充说明）.pdf", "supplement");
            var index = Scan(root);
            var person = index.Contents.Values.SelectMany(entries => entries).Single(entry => entry.Path == first);
            var plan = CollectionPlan.Build(index, new[] { person }, new[] { "入职协议" }).Single();
            Check(plan.Chosen == null && plan.Candidates.Count == 1 &&
                plan.MatchReason(plan.Candidates[0]).Contains("需确认"), "supplement alone requires confirmation");

            string signed = Write(first, "签署版_入职协议.pdf", "signed");
            string otherAgreement = Write(second, "入职协议_同名员工_HR0001_20260101.pdf", "other employee");
            index = Scan(root);
            var people = index.Contents.Values.SelectMany(entries => entries).Where(entry => entry.IsEmployeeFolder);
            var rows = CollectionPlan.Build(index, people, new[] { "入职协议" });
            var ambiguous = rows.Single(row => row.Department == "财务部");
            Check(ambiguous.Chosen == null && ambiguous.Candidates.Select(file => file.Path).OrderBy(path => path)
                .SequenceEqual(new[] { supplement, signed }.OrderBy(path => path)), "both signed and supplemental candidates remain visible");
            Check(rows.Single(row => row.Department == "销售部").Chosen.Path == otherAgreement,
                "same-named employees stay separate and unique standard match is preselected");
            ambiguous.Choose(ambiguous.Candidates.Single(file => file.Path == signed));
            Check(ambiguous.Chosen.Path == signed, "manually chosen candidate becomes ready");
            Check(RejectsOutput(root.ToUpperInvariant() + "\\", root) && RejectsOutput(first, root),
                "archive root and nested destination rejected before directory creation");
            var junction = Path.Combine(temp, "ArchiveAlias");
            if (Directory.Exists(junction))
                Check(RejectsOutput(junction, root), "junction into archive rejected");
            Check(ArchiveFiles.IsInsideArchive(@"\\SERVER\Share\Archive\员工\", @"\\server\share\archive") &&
                ArchiveFiles.IsInsideArchive(@"\\?\UNC\SERVER\Share\Archive", @"\\server\share\archive\") &&
                !ArchiveFiles.IsInsideArchive(@"\\server\share\archive-exports", @"\\server\share\archive"),
                "UNC aliases, boundary, case and separators are compared correctly");
            Check(ArchiveFiles.PrepareOutput(export + "\\", root).TrimEnd('\\') == export, "outside sibling remains an eligible output");
            Check(Scan(root).Files.Count == 3, "rejected in-archive export creates no indexed copy");

            string deleted = Write(first, "待删除.pdf", "synthetic");
            string moved = Write(first, "待移动.pdf", "synthetic");
            string deniedFolder = Directory.CreateDirectory(Path.Combine(second, "受限子目录")).FullName;
            string denied = Write(deniedFolder, "暂时不可读.pdf", "synthetic");
            index = Scan(root);
            var pending = index.Files.Where(file => new[] { signed, deleted, moved, denied }.Contains(file.Path)).ToArray();
            var deniedAcl = Directory.GetAccessControl(deniedFolder);
            byte[] originalAcl = deniedAcl.GetSecurityDescriptorBinaryForm();
            byte[] originalFileAcl = File.GetAccessControl(denied).GetSecurityDescriptorBinaryForm();
            deniedAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,
                FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            File.Delete(deleted);
            File.Move(moved, Path.Combine(temp, "moved.pdf"));
            try
            {
                Directory.SetAccessControl(deniedFolder, deniedAcl);
                var refreshed = Scan(root);
                var issues = refreshed.CheckSelectionAfterScan(pending);
                Check(issues.Count == 3 && issues[deleted] == ArchiveFiles.SourceMissing && issues[moved] == ArchiveFiles.SourceMissing &&
                    issues[denied] == ArchiveFiles.SourceUnavailable && !issues.ContainsKey(signed),
                    "refresh distinguishes deleted and moved files from denied directory and keeps healthy item");
                var preflight = ArchiveFiles.CheckSources(root, pending, refreshed);
                Check(preflight.Count == 3 && preflight[deleted] == ArchiveFiles.SourceMissing &&
                    preflight[denied] == ArchiveFiles.SourceUnavailable, "preflight distinguishes unavailable from missing");
            }
            finally
            {
                var restoredDirectory = new DirectorySecurity();
                restoredDirectory.SetSecurityDescriptorBinaryForm(originalAcl, AccessControlSections.Access);
                Directory.SetAccessControl(deniedFolder, restoredDirectory);
                var restoredFile = new FileSecurity();
                restoredFile.SetSecurityDescriptorBinaryForm(originalFileAcl, AccessControlSections.Access);
                File.SetAccessControl(denied, restoredFile);
            }
            Check(!ArchiveFiles.CheckSources(root, pending, Scan(root)).ContainsKey(denied),
                "restored access makes the selected file eligible again");

            Write(first, "同名材料.pdf", "first employee");
            string locked = Write(second, "同名材料.pdf", "second employee");
            var files = Scan(root).Files.Where(file => file.Name == "同名材料.pdf").ToList();
            var before = files.ToDictionary(file => file.Path, file => Hash(file.Path));
            CopyResult result;
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                result = ArchiveFiles.Copy(export, files, count => { });
            Check(result.Successes == 1 && result.Failures.Count == 1 && result.Failures[0].File.Path == locked &&
                Directory.GetFiles(export).Length == 1, "partial failure preserves successful output without temporary copies");
            result = ArchiveFiles.Copy(export, result.Failures.Select(failure => failure.File).ToList(), count => { });
            Check(result.Successes == 1 && Directory.GetFiles(export).Length == 2 &&
                Directory.GetFiles(export).Select(Hash).OrderBy(hash => hash).SequenceEqual(before.Values.OrderBy(hash => hash)),
                "retry copies only failed item and matches source hashes");
            Check(before.All(pair => pair.Value == Hash(pair.Key)), "copy and retry leave source hashes unchanged");
        }

        private static void MeasureLocalArchive(string temp)
        {
            string root = Directory.CreateDirectory(Path.Combine(temp, "LargeLocalArchive")).FullName;
            for (int employee = 0; employee < 200; employee++)
            {
                string directory = Directory.CreateDirectory(Path.Combine(root, "部门" + (employee / 20), "员工" + employee)).FullName;
                for (int file = 0; file < 100; file++)
                    Write(directory, "材料_" + file + (file % 2 == 0 ? ".pdf" : ".docx"), "synthetic");
            }
            var clock = Stopwatch.StartNew();
            var index = Scan(root);
            long scan = clock.ElapsedMilliseconds;
            var timings = new List<long>();
            for (int run = 0; run < 7; run++)
            {
                clock.Restart();
                var found = index.Search(null, "材料", new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "PDF" })
                    .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (found.Count != 10000) throw new InvalidOperationException("Benchmark result count");
                timings.Add(clock.ElapsedMilliseconds);
            }
            Console.WriteLine("Local scan at " + index.Files.Count + " actual files: " + scan +
                " ms; query and sort 10,000 results, median of seven: " + timings.OrderBy(ms => ms).ElementAt(3) + " ms (no UI rendering)");
        }

        private static int Main(string[] args)
        {
            try
            {
                TestRelease(args[0]);
                if (args.Contains("--benchmark")) MeasureLocalArchive(args[0]);
                Console.WriteLine("TOTAL " + passed);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
