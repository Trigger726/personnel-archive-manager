using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArchiveManager
{
    internal sealed class ArchiveSettings
    {
        public string Root = "";
        public int DepartmentLevel = 1;
        public int EmployeeLevel = 2;

        private static string SettingsFile
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PersonnelArchiveManager", "settings.txt");
            }
        }

        public static ArchiveSettings Load()
        {
            var settings = new ArchiveSettings();
            if (!File.Exists(SettingsFile)) return settings;
            var lines = File.ReadAllLines(SettingsFile);
            int department, employee;
            if (lines.Length == 3 && int.TryParse(lines[1], out department) &&
                int.TryParse(lines[2], out employee) && ValidLevels(department, employee))
            {
                settings.Root = lines[0];
                settings.DepartmentLevel = department;
                settings.EmployeeLevel = employee;
            }
            return settings;
        }

        public void Save()
        {
            string path = SettingsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temporary, new[] { Root, DepartmentLevel.ToString(), EmployeeLevel.ToString() });
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static bool ValidLevels(int department, int employee)
        {
            return department >= 1 && employee > department && employee <= 30;
        }
    }

    internal sealed class ArchiveEntry
    {
        private static readonly Dictionary<string, string> Formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".pdf", "PDF" }, { ".doc", "Word" }, { ".docx", "Word" }, { ".docm", "Word" }, { ".rtf", "Word" },
            { ".xls", "Excel" }, { ".xlsx", "Excel" }, { ".xlsm", "Excel" }, { ".csv", "Excel" },
            { ".ppt", "PowerPoint" }, { ".pptx", "PowerPoint" }, { ".pptm", "PowerPoint" },
            { ".jpg", "图片" }, { ".jpeg", "图片" }, { ".png", "图片" }, { ".webp", "图片" },
            { ".gif", "图片" }, { ".bmp", "图片" }, { ".tif", "图片" }, { ".tiff", "图片" }, { ".heic", "图片" },
            { ".zip", "压缩包" }, { ".rar", "压缩包" }, { ".7z", "压缩包" }, { ".txt", "文本" }
        };

        public string Path { get; private set; }
        public string Parent { get; private set; }
        public string Name { get; private set; }
        public string Department { get; private set; }
        public string Employee { get; private set; }
        public string EmployeePath { get; private set; }
        public string Format { get; private set; }
        public string FormatLabel { get; private set; }
        public DateTime? Modified { get; private set; }
        public bool IsFolder { get; private set; }
        public bool IsEmployeeFolder { get; private set; }

        public ArchiveEntry(FileSystemInfo item, string parent, string[] parentLevels, ArchiveSettings settings)
        {
            Path = item.FullName;
            Parent = parent;
            Name = item.Name;
            IsFolder = item is DirectoryInfo;
            string[] levels = IsFolder ? parentLevels.Concat(new[] { Name }).ToArray() : parentLevels;
            IsEmployeeFolder = IsFolder && levels.Length == settings.EmployeeLevel;
            Department = levels.Length >= settings.DepartmentLevel ? levels[settings.DepartmentLevel - 1] : "";
            Employee = levels.Length >= settings.EmployeeLevel ? levels[settings.EmployeeLevel - 1] : "";
            EmployeePath = Employee.Length > 0 ? string.Join("\\", levels.Take(settings.EmployeeLevel)) : "";
            Modified = item.LastWriteTime;

            string extension = item.Extension.ToUpperInvariant();
            string format;
            Format = IsFolder ? "文件夹" : Formats.TryGetValue(extension, out format) ? format : "其他";
            FormatLabel = IsFolder ? Format : extension.Length == 0 ? "无扩展名" : extension.Substring(1);
        }
    }

    internal sealed class ArchiveIndex
    {
        public readonly List<ArchiveEntry> Files = new List<ArchiveEntry>();
        public readonly Dictionary<string, List<ArchiveEntry>> Contents =
            new Dictionary<string, List<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Problems = new List<string>();
        private readonly HashSet<string> unreadablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void AddProblem(string path, string reason)
        {
            unreadablePaths.Add(path);
            Problems.Add(path + "　" + reason);
        }

        public Dictionary<string, string> CheckSelectionAfterScan(IEnumerable<ArchiveEntry> selected)
        {
            var remaining = new HashSet<string>(selected.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
            remaining.ExceptWith(Files.Select(file => file.Path));
            var issues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in remaining)
                issues.Add(path, WasUnreadable(path) ? ArchiveFiles.SourceUnavailable : ArchiveFiles.SourceMissing);
            return issues;
        }

        public bool WasUnreadable(string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
                if (unreadablePaths.Contains(current)) return true;
            return false;
        }

        public static ArchiveIndex Scan(ArchiveSettings settings, Action<int> progress)
        {
            var index = new ArchiveIndex();
            var pending = new Stack<Tuple<string, string[]>>();
            pending.Push(Tuple.Create(settings.Root, new string[0]));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                var entries = new List<ArchiveEntry>();
                index.Contents.Add(directory.Item1, entries);
                try
                {
                    foreach (var item in new DirectoryInfo(directory.Item1).EnumerateFileSystemInfos())
                    {
                        try
                        {
                            bool isLink = (item.Attributes & FileAttributes.ReparsePoint) != 0;
                            if (isLink && item is DirectoryInfo)
                            {
                                index.AddProblem(item.FullName, "已跳过链接目录");
                                continue;
                            }
                            var entry = new ArchiveEntry(item, directory.Item1, directory.Item2, settings);
                            entries.Add(entry);
                            if (entry.IsFolder)
                                pending.Push(Tuple.Create(entry.Path, directory.Item2.Concat(new[] { entry.Name }).ToArray()));
                            else
                                index.Files.Add(entry);
                        }
                        catch (Exception error)
                        {
                            if (!ArchiveFiles.IsFileError(error)) throw;
                            index.AddProblem(item.FullName, ArchiveFiles.ErrorText(error));
                        }

                        if (elapsed.ElapsedMilliseconds >= 150)
                        {
                            progress(index.Files.Count);
                            elapsed.Restart();
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!ArchiveFiles.IsFileError(error)) throw;
                    index.AddProblem(directory.Item1, ArchiveFiles.ErrorText(error));
                }
            }
            return index;
        }

        public IEnumerable<ArchiveEntry> Search(string folder, string query,
            HashSet<string> departments, HashSet<string> employees, HashSet<string> formats, HashSet<string> keywords = null)
        {
            var words = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return Files.Where(file => (folder == null || ArchiveFiles.IsWithin(file.Path, folder)) &&
                (departments.Count == 0 || departments.Contains(file.Department)) &&
                (employees.Count == 0 || employees.Contains(file.EmployeePath)) &&
                (formats.Count == 0 || formats.Contains(file.Format)) &&
                (keywords == null || keywords.Count == 0 || keywords.Any(word => file.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)) &&
                words.All(word => file.Path.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0));
        }
    }

    internal sealed class CopyFailure
    {
        public readonly ArchiveEntry File;
        public readonly string Reason;
        public CopyFailure(ArchiveEntry file, string reason) { File = file; Reason = reason; }
    }

    internal sealed class CopyResult
    {
        public readonly List<CopyFailure> Failures = new List<CopyFailure>();
        public int Successes;
    }

    internal static class ArchiveFiles
    {
        public const string SourceMissing = "找不到源文件";
        public const string SourceUnavailable = "源文件或所在目录暂时无法读取";

        public static bool IsWithin(string file, string folder)
        {
            return file.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFileError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is SecurityException ||
                error is ArgumentException || error is NotSupportedException;
        }

        public static string ErrorText(Exception error)
        {
            if (error is FileNotFoundException) return "找不到源文件。";
            if (error is DirectoryNotFoundException) return "找不到目录。";
            if (error is UnauthorizedAccessException || error is SecurityException) return "当前 Windows 账号无访问权限。";
            if (error is PathTooLongException) return "路径过长。";
            if ((error.HResult & 0xffff) == 112 || (error.HResult & 0xffff) == 39) return "目标磁盘空间不足。";
            return error.Message;
        }

        public static Dictionary<string, string> CheckSources(string root, IEnumerable<ArchiveEntry> files, ArchiveIndex index)
        {
            var issues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool? rootAvailable = null;
            foreach (var file in files)
            {
                try
                {
                    using (File.Open(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                }
                catch (Exception error)
                {
                    if (!IsFileError(error)) throw;
                    bool missing = error is FileNotFoundException || error is DirectoryNotFoundException;
                    // If the archive was unreachable, a missing path does not establish that the file was deleted.
                    if (missing && !index.WasUnreadable(file.Path))
                    {
                        if (!rootAvailable.HasValue) rootAvailable = Directory.Exists(root);
                        issues[file.Path] = rootAvailable.Value ? SourceMissing : SourceUnavailable;
                    }
                    else issues[file.Path] = SourceUnavailable;
                }
            }
            return issues;
        }

        public static bool IsInsideArchive(string directory, string root)
        {
            string candidate = NormalizeDirectory(directory).TrimEnd('\\', '/');
            string archive = NormalizeDirectory(root).TrimEnd('\\', '/');
            return StringComparer.OrdinalIgnoreCase.Equals(candidate, archive) || IsWithin(candidate, archive);
        }

        private static string NormalizeDirectory(string directory)
        {
            if (directory.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                directory = @"\\" + directory.Substring(8);
            else if (directory.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                directory = directory.Substring(4);
            string full = Path.GetFullPath(directory);
            return full.Length > Path.GetPathRoot(full).Length ? full.TrimEnd('\\', '/') : full;
        }

        public static void ValidateOutput(string directory, string root)
        {
            if (IsInsideArchive(directory, root) ||
                IsInsideArchive(ResolveDirectory(directory), ResolveDirectory(root)))
                throw new IOException("复制目标位于档案库内。请选择档案库之外的文件夹，避免副本被重新收录。");
        }

        private static string ResolveDirectory(string path)
        {
            const uint OpenExisting = 3;
            const uint BackupSemantics = 0x02000000; // Required to open a directory handle.
            using (var handle = OpenDirectory(NormalizeDirectory(path), 0, 7, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw FolderCheckError();
                var buffer = new StringBuilder(512);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length >= buffer.Capacity)
                {
                    buffer.Capacity = checked((int)length + 1);
                    length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                }
                if (length == 0 || length >= buffer.Capacity) throw FolderCheckError();
                return buffer.ToString();
            }
        }

        private static IOException FolderCheckError()
        {
            return new IOException("无法核对档案库与复制目标的位置。请确认两个目录均可访问后重试。",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint mode,
            uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);

        public static string PrepareOutput(string directory, string root = null)
        {
            if (root != null) ValidateOutput(directory, root);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) return directory;
            string name = "档案副本_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            for (int number = 1; ; number++)
            {
                string output = Path.Combine(directory, number == 1 ? name : name + "_" + number);
                if (CreateDirectory(output, IntPtr.Zero)) return output;
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (error != 183 && error != 80)
                    throw new IOException(new System.ComponentModel.Win32Exception(error).Message, unchecked((int)0x80070000) | error);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

        public static CopyResult Copy(string directory, IList<ArchiveEntry> files, Action<int> progress)
        {
            var result = new CopyResult();
            foreach (var file in files)
            {
                try
                {
                    CopyOne(directory, file);
                    result.Successes++;
                }
                catch (Exception error)
                {
                    if (!IsFileError(error)) throw;
                    result.Failures.Add(new CopyFailure(file, ErrorText(error)));
                }
                progress(result.Successes + result.Failures.Count);
            }
            return result;
        }

        private static void CopyOne(string directory, ArchiveEntry file)
        {
            string temporary = Path.Combine(directory, ".档案复制_" + Guid.NewGuid().ToString("N") + ".tmp");
            bool temporaryCreated = false;
            try
            {
                using (var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.WriteThrough))
                {
                    temporaryCreated = true;
                    source.CopyTo(target);
                }

                string extension = Path.GetExtension(file.Name);
                string stem = string.Join("_", new[] { file.Department, file.Employee, Path.GetFileNameWithoutExtension(file.Name) }
                    .Where(part => part.Length > 0));
                for (int number = 1; ; number++)
                {
                    string suffix = number == 1 ? "" : "_" + number;
                    string outputName = LimitName(stem, extension, suffix);
                    string output = Path.Combine(directory, outputName);
                    try
                    {
                        File.Move(temporary, output);
                        temporaryCreated = false;
                        return;
                    }
                    catch (IOException error)
                    {
                        // A competing copy can claim a name between attempts.
                        int code = error.HResult & 0xffff;
                        if (code != 80 && code != 183 && !Directory.Exists(output)) throw;
                    }
                }
            }
            finally
            {
                if (temporaryCreated) File.Delete(temporary);
            }
        }

        private static string LimitName(string stem, string extension, string suffix)
        {
            if (stem.Length + extension.Length + suffix.Length <= 255) return stem + suffix + extension;
            int limit = 255 - suffix.Length;
            if (extension.Length >= limit) return Shorten(stem + extension, limit) + suffix;
            return Shorten(stem, limit - extension.Length) + suffix + extension;
        }

        private static string Shorten(string value, int length)
        {
            if (char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length).TrimEnd('.', ' ');
        }
    }
}
