using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Diagnostics;

namespace ArchiveManager
{
    internal static class VerifyKeywords
    {
        private static int passed;
        private static string output;

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
            passed++;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            output = args[0];
            string root = Path.Combine(args[0], "archive");
            string first = Directory.CreateDirectory(Path.Combine(root, "财务部", "示例甲_HR1001")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(root, "研发部", "示例乙_HR2001")).FullName;
            foreach (string name in new[]
            {
                "体检报告_示例甲_HR1001_2026-11-16.pdf",
                "示例甲_离职交接单_20261116.ofd",
                "入职协议补充说明_示例甲_HR1001.pdf",
                "入职协议_示例甲_HR1001.pdf",
                "扫描件_1234.pdf",
                "HR1001.pdf",
                "~$薪资证明.docx"
            })
                File.WriteAllText(Path.Combine(first, name), "synthetic fixture");
            File.WriteAllText(Path.Combine(second, "体检报告_示例乙_HR2001_20261116.docx"), "synthetic fixture");
            File.WriteAllText(Path.Combine(second, "工作证明（扫描件）.pdf"), "synthetic fixture");
            File.WriteAllText(Path.Combine(second, "W-2.pdf"), "synthetic fixture");
            File.WriteAllText(Path.Combine(second, "desktop.ini"), "synthetic fixture");

            string store = Path.Combine(args[0], "settings", "material-keywords.txt");
            var catalog = new MaterialKeywordCatalog(store);
            catalog.Load();
            var index = ArchiveIndex.Scan(new ArchiveSettings { Root = root }, count => { });
            var suggestions = catalog.Discover(index.Files);
            Check(suggestions.Select(item => item.Keyword).OrderBy(word => word).SequenceEqual(
                new[] { "体检报告", "离职交接单", "工作证明", "W-2" }.OrderBy(word => word)),
                "infer document names without generic names, employee names, IDs, or temporary files");
            Check(suggestions.Single(item => item.Keyword == "体检报告").Count == 2,
                "combine matching types across departments");
            Check(!File.Exists(store) && catalog.Keywords.Length == 12 && !catalog.Keywords.Contains("体检报告"),
                "detection alone leaves the catalog untouched");
            Check(suggestions.All(item => index.Files.Any(file => file.Name == item.Example &&
                file.Name.IndexOf(item.Keyword, StringComparison.OrdinalIgnoreCase) >= 0)),
                "suggested keywords are usable as filename search terms");

            catalog.SaveChanges(new[] { "体检报告", "  收款确认单  " }, new[] { "离职交接单" });
            var restarted = new MaterialKeywordCatalog(store);
            restarted.Load();
            Check(restarted.Keywords.Contains("体检报告") && restarted.Keywords.Contains("收款确认单") &&
                !restarted.Keywords.Contains("离职交接单"), "confirmation and manual additions persist across reload");
            Check(restarted.Discover(index.Files).Select(item => item.Keyword).OrderBy(word => word)
                .SequenceEqual(new[] { "工作证明", "W-2" }.OrderBy(word => word)), "accepted and ignored terms are not suggested again");
            restarted.SaveChanges(new[] { "离职交接单", "w-2", "W-2", "入职协议", "无效/路径" }, new[] { "劳动合同" });
            Check(restarted.Keywords.Contains("离职交接单") &&
                restarted.Keywords.Count(word => word.Equals("W-2", StringComparison.OrdinalIgnoreCase)) == 1 &&
                restarted.Keywords.Contains("劳动合同") && !restarted.Keywords.Contains("无效/路径"),
                "manual add restores ignored terms, deduplicates case, and protects presets");

            string detected = restarted.Keywords.Single(word => word == "体检报告");
            var matches = index.Search(null, "", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>(new[] { detected }, StringComparer.OrdinalIgnoreCase)).ToList();
            var staff = index.Contents.Values.SelectMany(entries => entries).Where(file => file.IsEmployeeFolder);
            var plan = CollectionPlan.Build(index, staff, new[] { detected });
            Check(matches.Count == 2 && plan.Count == 2 && plan.All(item => item.Candidates.Count == 1),
                "accepted term works in both keyword filtering and checklist collection");
            string flat = Directory.CreateDirectory(Path.Combine(args[0], "chosen-folder")).FullName;
            File.WriteAllText(Path.Combine(flat, "张三_健康检查报告_20261116.pdf"), "fixture");
            File.WriteAllText(Path.Combine(flat, "李四_健康检查报告_20261115.docx"), "fixture");
            string child = Directory.CreateDirectory(Path.Combine(flat, "subfolder")).FullName;
            File.WriteAllText(Path.Combine(child, "张三_安全培训承诺书.pdf"), "fixture");
            File.WriteAllText(Path.Combine(flat, "劳动合同.pdf"), "fixture");
            File.WriteAllText(Path.Combine(flat, "离职交接单.pdf"), "fixture");
            var flatIndex = ArchiveIndex.Scan(new ArchiveSettings { Root = flat,
                DepartmentLevel = int.MaxValue, EmployeeLevel = int.MaxValue }, count => { });
            var found = restarted.Discover(flatIndex.Files, true);
            Check(found.Count == 4 && found.Single(item => item.Keyword == "健康检查报告").Count == 2 &&
                found.Any(item => item.Keyword == "安全培训承诺书"),
                "selected flat folder and subfolders infer new types independently of presets");
            Check(found.Single(item => item.Keyword == "劳动合同").IsAdded &&
                !found.Single(item => item.Keyword == "健康检查报告").IsAdded,
                "folder results include existing terms and distinguish new suggestions");
            Check(!restarted.Keywords.Contains("健康检查报告") &&
                !found.Any(item => item.Keyword == "体检报告"),
                "folder discovery stays within chosen directory and waits for confirmation");
            VerifyDialogs(restarted, index);
            VerifyFolderLifecycle(restarted, flat);
            Console.WriteLine("TOTAL " + passed);
        }

        private static void VerifyFolderLifecycle(MaterialKeywordCatalog catalog, string folder)
        {
            var session = new KeywordFolderScan();
            session.Scan(folder, count => { }, CancellationToken.None);
            session.Suggestions = catalog.Discover(session.Index.Files, true);
            Check(session.Suggestions.All(item => item.Files.Count == item.Count &&
                item.Files.All(file => file.Path.StartsWith(folder) &&
                    file.Name.IndexOf(item.Keyword, StringComparison.OrdinalIgnoreCase) >= 0)),
                "every grouped file has an inspectable source path and supports its keyword");
            catalog.SaveChanges(new string[0], new[] { "健康检查报告" });
            catalog.UpdateStates(session.Suggestions);
            Check(session.Suggestions.Single(item => item.Keyword == "健康检查报告").IsIgnored,
                "ignored folder result shows ignored state");
            catalog.SaveChanges(new[] { "健康检查报告" }, new string[0]);
            catalog.UpdateStates(session.Suggestions);
            Check(session.Suggestions.Single(item => item.Keyword == "健康检查报告").IsAdded,
                "ignored result can be explicitly readded");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                bool stopped = false;
                try { session.Scan(folder, count => { }, cancel.Token); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped && session.Folder == folder && session.Index.Files.Count == 5,
                    "cancelled rescan preserves previous results");
                stopped = false;
                try { catalog.Discover(session.Index.Files, true, cancel.Token); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "keyword inference honors cancellation");
            }
            var empty = Directory.CreateDirectory(Path.Combine(output, "empty")).FullName;
            var scan = new KeywordFolderScan();
            scan.Scan(empty, count => { }, CancellationToken.None);
            Check(scan.Index.Files.Count == 0 && scan.Index.Problems.Count == 0,
                "empty folder is distinct from a failed read");
            scan.Scan(Path.Combine(empty, "missing"), count => { }, CancellationToken.None);
            Check(scan.Index.Problems.Count == 1 && scan.Index.Files.Count == 0,
                "unreadable directory retains path and error");
            File.WriteAllText(Path.Combine(empty, "扫描件.txt"), "fixture");
            File.WriteAllText(Path.Combine(empty, new string('长', 70) + ".txt"), "fixture");
            scan.Scan(empty, count => { }, CancellationToken.None);
            scan.Suggestions = catalog.Discover(scan.Index.Files, true);
            Check(scan.Suggestions.Count == 0 && scan.Unrecognized(scan.Suggestions).Count == 2,
                "generic and overlong names remain available in unrecognized file list");
        }

        private static void VerifyDialogs(MaterialKeywordCatalog catalog, ArchiveIndex index)
        {
            var app = new Application();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml"))
                app.Resources = (ResourceDictionary)XamlReader.Load(stream);
            var owner = new Window { Width = 640, Height = 470, ShowInTaskbar = false };
            owner.FontFamily = Appearance.InterfaceFont(Path.GetFullPath("人事档案管理-关键词自检升级版"));
            owner.Show();
            MaterialKeywordDialog.SaveKeywords save = (window, add, ignore) =>
            {
                catalog.SaveChanges(add, ignore);
                return true;
            };
            var suggestions = catalog.Discover(index.Files);
            InteractWithDialog(owner, dialog =>
            {
                var box = VisualChildren<CheckBox>(dialog).Single();
                Check(box.IsEnabled && box.IsChecked == false, "suggestion is selectable and starts unchecked");
                box.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                Press(dialog, "加入勾选项");
            }, () => MaterialKeywordDialog.ReviewSuggestions(owner, suggestions, save));
            Check(catalog.Keywords.Contains("工作证明"), "checking a suggestion and confirming saves it");

            var all = catalog.Discover(index.Files, true);
            InteractWithDialog(owner, dialog =>
            {
                Capture(dialog, "识别结果-正常.png");
                dialog.Width = dialog.MinWidth;
                dialog.Height = dialog.MinHeight;
                Capture(dialog, "识别结果-最小.png");
                Press(dialog, "稍后");
            }, () => MaterialKeywordDialog.ReviewSuggestions(owner, all, save, index.Files[0].Parent));

            InteractWithDialog(owner, dialog =>
            {
                var input = VisualChildren<TextBox>(dialog).Single();
                int count = catalog.Keywords.Length;
                input.Text = " ";
                Press(dialog, "添加关键词");
                input.Text = new string('长', 65);
                Press(dialog, "添加关键词");
                input.Text = "资料/无效";
                Press(dialog, "添加关键词");
                Check(catalog.Keywords.Length == count, "blank, invalid and long manual input never silently saves truncated text");
                input.Text = "档案借阅单";
                Press(dialog, "添加关键词");
                input.Text = "档案借阅单";
                Press(dialog, "添加关键词");
                Check(catalog.Keywords.Count(word => word == "档案借阅单") == 1, "manual duplicate stays single");
                Capture(dialog, "关键词管理-正常.png");
                dialog.Width = dialog.MinWidth;
                dialog.Height = dialog.MinHeight;
                Capture(dialog, "关键词管理-最小.png");
                Press(dialog, "完成");
            }, () => MaterialKeywordDialog.Manage(owner, catalog, () => catalog.Discover(index.Files), save));
            Check(catalog.Keywords.Contains("档案借阅单"), "management dialog saves a manually entered keyword");
            owner.Close();
        }

        private static void Capture(Window dialog, string filename)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (sender, args) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            dialog.UpdateLayout();
            var content = (FrameworkElement)dialog.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight,
                96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                drawing.DrawRectangle(Brushes.White, null, bounds);
                drawing.DrawRectangle(new VisualBrush(content), null, bounds);
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, filename))) encoder.Save(stream);
            Check(VisualChildren<Button>(dialog).Where(button => button.IsVisible)
                .All(button => button.ActualWidth > 0 && button.ActualHeight > 0),
                "visible dialog actions have layout at " + filename);
        }

        private static void InteractWithDialog(Window owner, Action<Window> interact, Action showDialog)
        {
            Exception failure = null;
            owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = owner.OwnedWindows.OfType<Dialog>().Single(window => window.IsVisible);
                try { interact(dialog); }
                catch (Exception error) { failure = error; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }));
            showDialog();
            if (failure != null) throw failure;
        }

        private static void Press(Window dialog, string label)
        {
            VisualChildren<Button>(dialog).Single(button => Equals(button.Content, label))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T) yield return (T)child;
                foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
            }
        }
    }
}
