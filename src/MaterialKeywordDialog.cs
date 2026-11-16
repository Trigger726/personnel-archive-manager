using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace ArchiveManager
{
    internal static class MaterialKeywordDialog
    {
        public delegate bool SaveKeywords(Window owner, IEnumerable<string> add, IEnumerable<string> ignore);

        public static void Manage(Window owner, MaterialKeywordCatalog catalog, Func<List<MaterialKeywordSuggestion>> suggestions,
            SaveKeywords save, KeywordFolderScan previousScan = null)
        {
            var session = previousScan ?? new KeywordFolderScan();
            var dialog = new Dialog(owner, "管理资料关键词", 780, 580,
                "新增的关键词会同步出现在资料筛选和按清单收集里。");
            dialog.MinWidth = 720;
            dialog.MinHeight = 520;
            var body = new DockPanel();
            var scanPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(scanPanel, Dock.Top);
            body.Children.Add(scanPanel);
            var scanStatus = new TextBlock { Text = "选择一个文件夹，识别其中及子文件夹中的资料关键词。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetLiveSetting(scanStatus, AutomationLiveSetting.Polite);
            scanStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Button scan = null;
            var field = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var label = new TextBlock { Text = "手动新增" };
            DockPanel.SetDock(label, Dock.Top);
            body.Children.Add(label);
            DockPanel.SetDock(field, Dock.Top);
            body.Children.Add(field);
            var hint = new TextBlock { Text = "例如：体检报告、离职证明", Margin = new Thickness(0, 5, 0, 10) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            AutomationProperties.SetLiveSetting(hint, AutomationLiveSetting.Polite);
            DockPanel.SetDock(hint, Dock.Top);
            body.Children.Add(hint);
            var input = new TextBox();
            AutomationProperties.SetName(input, "新资料关键词");
            var table = Dialog.Table(null, Dialog.Column("资料关键词", "Keyword", 1, true),
                Dialog.Column("来源", "Source", 115));
            body.Children.Add(table);
            dialog.ShowContent(body);

            Button remove = null;
            Button detected = null;
            Button lastResults = null;
            Action refresh = () =>
            {
                table.ItemsSource = catalog.Keywords.Select(word => new KeywordRow(word, catalog.IsPreset(word))).ToList();
                remove.IsEnabled = false;
                int count = suggestions().Count;
                detected.Content = "识别建议（" + count + "）";
                detected.IsEnabled = count > 0;
                lastResults.IsEnabled = session.Index != null;
            };
            CancellationTokenSource scanCancellation = null;
            bool closed = false;
            var cancel = dialog.AddButton("取消扫描", () =>
            {
                if (scanCancellation != null) scanCancellation.Cancel();
                scanStatus.Text = "正在取消，等待读取返回…";
            });
            cancel.Visibility = Visibility.Collapsed;
            Action reviewLast = () =>
            {
                catalog.UpdateStates(session.Suggestions);
                ReviewSuggestions(dialog, session.Suggestions, save, session.Folder);
                catalog.UpdateStates(session.Suggestions);
            };
            lastResults = Program.Button("查看上次结果", reviewLast);
            scan = Program.Button("指定文件夹识别…", async () =>
            {
                if (scanCancellation != null) return;
                string folder = FolderPicker.Select(dialog, "选择要识别资料关键词的文件夹");
                if (folder == null) return;
                var cancellation = new CancellationTokenSource();
                scanCancellation = cancellation;
                body.IsEnabled = false;
                detected.IsEnabled = false;
                remove.IsEnabled = false;
                scanStatus.Text = "正在扫描文件名…";
                cancel.Visibility = Visibility.Visible;
                var progress = new Progress<int>(count =>
                {
                    if (!closed && scanCancellation == cancellation && !cancellation.IsCancellationRequested)
                        scanStatus.Text = "已扫描 " + count + " 个文件…";
                });
                try
                {
                    var result = await Task.Run(() =>
                    {
                        var scanned = new KeywordFolderScan();
                        scanned.Scan(folder, count => ((IProgress<int>)progress).Report(count), cancellation.Token);
                        scanned.Suggestions = catalog.Discover(scanned.Index.Files, true, cancellation.Token);
                        return scanned;
                    });
                    if (closed) return;
                    cancellation.Token.ThrowIfCancellationRequested();
                    session.Apply(result);
                    scanStatus.Text = "已扫描 " + result.Index.Files.Count + " 个文件，识别出 " +
                        result.Suggestions.Count + " 个关键词。" +
                        (result.Index.Problems.Count == 0 ? "" : " 部分位置读取失败，结果可能不完整。");
                    scanStatus.ToolTip = folder;
                    reviewLast();
                }
                catch (OperationCanceledException)
                {
                    if (!closed) scanStatus.Text = "已取消扫描，可重新选择文件夹。";
                }
                catch (Exception error)
                {
                    if (closed) return;
                    scanStatus.Text = "识别失败，可重新选择文件夹。";
                    MessageBox.Show(dialog, error.Message, "识别失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    scanCancellation = null;
                    cancellation.Dispose();
                    if (!closed)
                    {
                        cancel.Visibility = Visibility.Collapsed;
                        body.IsEnabled = true;
                        refresh();
                    }
                }
            }, true);
            scan.HorizontalAlignment = HorizontalAlignment.Left;
            scan.Margin = new Thickness(0);
            var scanActions = new WrapPanel();
            scanActions.Children.Add(scan);
            scanActions.Children.Add(lastResults);
            var unrecognized = Program.Button("查看未识别文件", () =>
                ShowFiles(dialog, "未识别文件", session.Unrecognized(session.Suggestions),
                    "以下文件尚未提取出关键词，可能是临时文件、泛称或超长名称，可结合文件名手动添加关键词。"));
            unrecognized.IsEnabled = session.Index != null;
            scanActions.Children.Add(unrecognized);
            var problems = Program.Button("读取问题", () =>
            {
                var details = new Dialog(dialog, "读取问题", 760, 460, session.Folder);
                details.ShowContent(Dialog.Table(session.Index.Problems, Dialog.Column("路径及原因", ".", 1, true)));
                details.AddClose();
                details.ShowDialog();
            });
            problems.IsEnabled = session.Index != null && session.Index.Problems.Count > 0;
            scanActions.Children.Add(problems);
            var refreshControls = refresh;
            refresh = () =>
            {
                refreshControls();
                unrecognized.IsEnabled = session.Index != null;
                problems.IsEnabled = session.Index != null && session.Index.Problems.Count > 0;
            };
            scanPanel.Children.Add(scanActions);
            scanPanel.Children.Add(scanStatus);
            dialog.Closed += (sender, args) =>
            {
                closed = true;
                if (scanCancellation != null) scanCancellation.Cancel();
            };
            Action add = () =>
            {
                string word = input.Text.Trim();
                if (!MaterialKeywordCatalog.IsValid(word))
                {
                    hint.Text = "请输入 1 到 64 个字符，避开换行及文件名不允许的符号。";
                    return;
                }
                if (catalog.Keywords.Contains(word, StringComparer.OrdinalIgnoreCase))
                {
                    hint.Text = "这个关键词已经存在。";
                    return;
                }
                if (!save(dialog, new[] { word }, new string[0])) return;
                input.Clear();
                hint.Text = "已添加「" + word + "」。";
                refresh();
                input.Focus();
            };
            var addButton = Program.Button("添加关键词", add, true);
            DockPanel.SetDock(addButton, Dock.Right);
            field.Children.Add(addButton);
            field.Children.Add(input);
            input.PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.Enter) return;
                args.Handled = true;
                add();
            };
            detected = dialog.AddButton("识别建议", () =>
            {
                ReviewSuggestions(dialog, suggestions(), save);
                refresh();
            });
            remove = dialog.AddButton("移除自定义关键词", () =>
            {
                var row = table.SelectedItem as KeywordRow;
                if (row == null || row.IsPreset) return;
                if (!save(dialog, new string[0], new[] { row.Keyword })) return;
                hint.Text = "已移除「" + row.Keyword + "」。";
                refresh();
            });
            table.SelectionChanged += (sender, args) =>
            {
                var row = table.SelectedItem as KeywordRow;
                remove.IsEnabled = row != null && !row.IsPreset;
            };
            dialog.AddClose("完成");
            dialog.Loaded += (sender, args) => input.Focus();
            refresh();
            dialog.ShowDialog();
        }

        public static void ReviewSuggestions(Window owner, IList<MaterialKeywordSuggestion> suggestions, SaveKeywords save,
            string sourceFolder = null)
        {
            if (suggestions.Count == 0 && sourceFolder == null) return;
            foreach (var suggestion in suggestions) suggestion.Checked = false;
            var dialog = new Dialog(owner, sourceFolder == null ? "加入新资料关键词？" : "文件夹识别结果", 1000, 570,
                suggestions.Count == 0 ? "没有提取到资料关键词，可返回查看未识别文件和读取问题。" :
                "从文件名识别出 " + suggestions.Count + " 个资料关键词。核对示例，勾选后加入。");
            dialog.MinWidth = 820;
            dialog.MinHeight = 490;
            var table = Dialog.Table(suggestions, Dialog.Column("资料关键词", "Keyword", 175),
                Dialog.Column("状态", "Status", 110),
                Dialog.Column("文件数", "Count", 64), Dialog.Column("示例文件名", "Example", 220),
                Dialog.Column("来源路径", "SourcePath", 1, true));
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("Checked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetBinding(AutomationProperties.NameProperty, new Binding("Keyword"));
            check.SetBinding(UIElement.IsEnabledProperty, new Binding("CanAdd"));
            table.Columns.Insert(0, new DataGridTemplateColumn
            {
                Header = "选择",
                Width = 55,
                CanUserSort = false,
                CellTemplate = new DataTemplate { VisualTree = check }
            });
            var body = new DockPanel();
            var note = new TextBlock { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
                Text = sourceFolder == null ? "选「稍后」可以在「管理资料关键词」里重新查看。" :
                    "已添加的关键词标注在列表中。其余结果请核对后添加，也可手动输入更准确的关键词。" };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
            DockPanel.SetDock(note, Dock.Top);
            body.Children.Add(note);
            if (sourceFolder != null)
            {
                var location = new TextBlock { Text = sourceFolder, ToolTip = sourceFolder,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
                DockPanel.SetDock(location, Dock.Top);
                body.Children.Add(location);
            }
            body.Children.Add(table);
            dialog.ShowContent(body);
            Action<bool> apply = add =>
            {
                var selected = suggestions.Where(item => item.Checked && item.CanAdd).Select(item => item.Keyword).ToArray();
                if (selected.Length == 0)
                {
                    note.Text = "请先勾选至少一个建议。";
                    return;
                }
                if (save(dialog, add ? selected : new string[0], add ? new string[0] : selected))
                    dialog.Close();
            };
            dialog.AddClose("稍后");
            var details = dialog.AddButton("查看匹配文件", () =>
            {
                var selected = table.SelectedItem as MaterialKeywordSuggestion;
                if (selected != null)
                    ShowFiles(dialog, selected.Keyword, selected.Files, selected.Basis);
            });
            details.IsEnabled = false;
            table.SelectionChanged += (sender, args) => details.IsEnabled = table.SelectedItem != null;
            dialog.AddButton("忽略勾选项", () => apply(false));
            dialog.AddButton("加入勾选项", () => apply(true), true);
            dialog.ShowDialog();
        }

        private static void ShowFiles(Window owner, string title, IEnumerable<ArchiveEntry> files, string description)
        {
            var dialog = new Dialog(owner, title, 1000, 520, description);
            dialog.MinWidth = 820;
            dialog.ShowContent(Dialog.Table(files, Dialog.Column("文件名", "Name", 280),
                Dialog.Column("来源路径", "Path", 1, true),
                Dialog.Column("修改时间", "Modified", 160)));
            dialog.AddClose();
            dialog.ShowDialog();
        }

        private sealed class KeywordRow
        {
            public string Keyword { get; private set; }
            public bool IsPreset { get; private set; }
            public string Source { get { return IsPreset ? "预设" : "已添加"; } }

            public KeywordRow(string keyword, bool isPreset)
            {
                Keyword = keyword;
                IsPreset = isPreset;
            }
        }
    }
}
