using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace ArchiveManager
{
    internal static class CollectionDialog
    {
        public static IList<ArchiveEntry> Show(Window owner, ArchiveIndex index, string folder, string[] materials,
            Action<ArchiveEntry, Window> openFile)
        {
            var people = index.Contents.Values.SelectMany(entries => entries).Where(entry => entry.IsEmployeeFolder)
                .OrderBy(entry => entry.Department).ThenBy(entry => entry.Employee)
                .Select(entry => new Option(entry.EmployeePath.Replace("\\", " / "), entry)).ToList();
            var documents = materials.Select(material => new Option(material)).ToList();
            if (people.Count == 0)
            {
                MessageBox.Show(owner, "当前索引里没有识别到员工文件夹。请检查设置中的员工层级并刷新。", "按清单收集",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return new ArchiveEntry[0];
            }

            foreach (var person in people)
                person.Checked = folder != null && (ArchiveFiles.IsWithin(person.Folder.Path, folder) ||
                    ArchiveFiles.IsWithin(folder, person.Folder.Path) ||
                    StringComparer.OrdinalIgnoreCase.Equals(person.Folder.Path, folder));
            documents.First(item => item.Label == "入职协议").Checked = true;
            documents.First(item => item.Label == "劳动合同").Checked = true;

            var dialog = new Dialog(owner, "按清单收集", 825, 560, "勾选员工和所需资料，再核对每个人的文件。");
            var body = new DockPanel();
            var count = new TextBlock { Margin = new Thickness(0, 10, 0, 0) };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(count, Dock.Bottom);
            body.Children.Add(count);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var peoplePanel = Options("员工", people, true);
            peoplePanel.Margin = new Thickness(0, 0, 14, 0);
            grid.Children.Add(peoplePanel);
            var materialPanel = Options("需要的资料", documents, false);
            Grid.SetColumn(materialPanel, 1);
            grid.Children.Add(materialPanel);
            body.Children.Add(grid);
            dialog.ShowContent(body);
            dialog.AddClose("取消");
            IList<ArchiveEntry> result = new ArchiveEntry[0];
            var check = dialog.AddButton("核对清单", () =>
            {
                var rows = CollectionPlan.Build(index, people.Where(item => item.Checked).Select(item => item.Folder),
                    documents.Where(item => item.Checked).Select(item => item.Label).ToList());
                result = Review(dialog, rows, index.Problems.Count > 0, openFile);
                if (result.Count > 0) dialog.Close();
            }, true);
            int employeeCount = people.Count(person => person.Checked);
            int documentCount = documents.Count(document => document.Checked);
            Action update = () =>
            {
                count.Text = employeeCount + " 名员工 × " + documentCount + " 种资料，共 " +
                    ((long)employeeCount * documentCount) + " 项";
                check.IsEnabled = employeeCount > 0 && documentCount > 0;
            };
            foreach (var option in people)
                option.PropertyChanged += (sender, args) => { employeeCount += option.Checked ? 1 : -1; update(); };
            foreach (var option in documents)
                option.PropertyChanged += (sender, args) => { documentCount += option.Checked ? 1 : -1; update(); };
            update();
            dialog.ShowDialog();
            return result;
        }

        private static DockPanel Options(string title, IList<Option> options, bool searchable)
        {
            var pane = new DockPanel();
            var label = new TextBlock { Text = title, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(label, Dock.Top);
            pane.Children.Add(label);
            var list = new ListBox { ItemsSource = options };
            list.SetResourceReference(Control.BorderBrushProperty, "Rule");
            var template = new DataTemplate();
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ContentControl.ContentProperty, new Binding("Label"));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding("Checked") { Mode = BindingMode.TwoWay });
            checkBox.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 5, 5, 5));
            template.VisualTree = checkBox;
            list.ItemTemplate = template;
            if (searchable)
            {
                var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var search = new TextBox { MinWidth = 105 };
                AutomationProperties.SetName(search, "按部门、姓名或工号筛选员工");
                search.ToolTip = "按部门、姓名或工号筛选员工";
                var searchField = new Grid();
                searchField.Children.Add(search);
                var hint = new TextBlock
                {
                    Text = "部门、姓名或工号", IsHitTestVisible = false,
                    Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                searchField.Children.Add(hint);
                var clear = Program.Button("清空", () => SetChecked(options, false));
                var select = Program.Button("全选匹配", () => SetChecked((IEnumerable<Option>)list.ItemsSource, true));
                DockPanel.SetDock(clear, Dock.Right);
                DockPanel.SetDock(select, Dock.Right);
                bar.Children.Add(clear);
                bar.Children.Add(select);
                bar.Children.Add(searchField);
                search.TextChanged += (sender, args) =>
                {
                    hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    var words = search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    list.ItemsSource = options.Where(option => words.All(word =>
                        option.Label.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                    select.IsEnabled = list.Items.Count > 0;
                };
                DockPanel.SetDock(bar, Dock.Top);
                pane.Children.Add(bar);
            }
            pane.Children.Add(list);
            return pane;
        }

        private static void SetChecked(IEnumerable<Option> options, bool value)
        {
            foreach (var option in options) option.Checked = value;
        }

        private static IList<ArchiveEntry> Review(Window owner, IList<CollectionItem> rows, bool incomplete,
            Action<ArchiveEntry, Window> openFile)
        {
            var dialog = new Dialog(owner, "核对收集清单", 855, 530,
                "每行对应一名员工的一种资料。双击查看候选；同一文件只加入一次。");
            var panel = new DockPanel();
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var outstanding = new CheckBox { Content = "只看待处理", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(outstanding, Dock.Right);
            bar.Children.Add(outstanding);
            var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(summary);
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);
            if (incomplete)
            {
                var warning = new TextBlock { Text = "部分目录未能读取。「未找到」请结合刷新时的读取问题核对。", Margin = new Thickness(0, 0, 0, 9) };
                warning.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                DockPanel.SetDock(warning, Dock.Top);
                panel.Children.Add(warning);
            }
            var table = Dialog.Table(rows, Dialog.Column("员工", "Employee", 128), Dialog.Column("部门", "Department", 75),
                Dialog.Column("所需资料", "Material", 112), Dialog.Column("状态", "Status", 90),
                Dialog.Column("已确认文件", "FileName", 1, true));
            panel.Children.Add(table);
            dialog.ShowContent(panel);
            IList<ArchiveEntry> chosen = new ArchiveEntry[0];
            dialog.AddClose("返回修改");
            Action inspectRow = null;
            var inspect = dialog.AddButton("查看候选", () => inspectRow());
            var missingRows = rows.Where(row => row.Candidates.Count == 0).ToList();
            var copyMissing = dialog.AddButton("复制未找到项", () =>
            {
                try
                {
                    Clipboard.SetText("部门\t员工\t资料" + Environment.NewLine + string.Join(Environment.NewLine,
                        missingRows.Select(row => row.Department + "\t" + row.Employee + "\t" + row.Material)));
                    summary.Text = "已复制 " + missingRows.Count + " 项，可粘贴到表格";
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    MessageBox.Show(dialog, "剪贴板暂时无法写入，请重试。", "复制未找到项",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
            copyMissing.IsEnabled = missingRows.Count > 0;
            var add = dialog.AddButton("加入待复制", () =>
            {
                chosen = ChosenFiles(rows).ToList();
                dialog.Close();
            }, true);

            Action updateInspect = () =>
            {
                var row = table.SelectedItem as CollectionItem;
                inspect.IsEnabled = row != null && row.Candidates.Count > 0;
            };
            Action refresh = () =>
            {
                int ready = rows.Count(row => row.Chosen != null);
                int missing = missingRows.Count;
                summary.Text = "可收集 " + ready + " 项    待确认 " + (rows.Count - ready - missing) + " 项    未找到 " + missing + " 项";
                int uniqueFiles = ChosenFiles(rows).Count();
                summary.ToolTip = "已确认 " + ready + " 项，对应 " + uniqueFiles + " 份独立文件；重复文件自动合并。";
                add.IsEnabled = ready > 0;
                add.Content = "加入待复制（" + uniqueFiles + "）";
                table.ItemsSource = outstanding.IsChecked == true ? rows.Where(row => row.Chosen == null).ToList() : rows.ToList();
                updateInspect();
            };
            inspectRow = () =>
            {
                var row = table.SelectedItem as CollectionItem;
                if (row == null || row.Candidates.Count == 0) return;
                var file = ChooseFile(dialog, row, openFile);
                if (file == null) return;
                row.Choose(file);
                refresh();
                table.SelectedItem = row;
            };
            table.MouseDoubleClick += (sender, args) =>
            {
                var row = ItemsControl.ContainerFromElement(table, args.OriginalSource as DependencyObject) as DataGridRow;
                if (row == null) return;
                table.SelectedItem = row.Item;
                inspectRow();
            };
            table.SelectionChanged += (sender, args) => updateInspect();
            outstanding.Checked += (sender, args) => refresh();
            outstanding.Unchecked += (sender, args) => refresh();
            refresh();
            dialog.ShowDialog();
            return chosen;
        }

        private static IEnumerable<ArchiveEntry> ChosenFiles(IEnumerable<CollectionItem> rows)
        {
            return rows.Where(row => row.Chosen != null).Select(row => row.Chosen)
                .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
        }

        private static ArchiveEntry ChooseFile(Window owner, CollectionItem row, Action<ArchiveEntry, Window> openFile)
        {
            var dialog = new Dialog(owner, "确认档案文件", 855, 430,
                row.EmployeeFolder.EmployeePath + " / " + row.Material + "。请按来源和文件内容核对材料。");
            var date = Dialog.Column("修改时间", "Modified", 132);
            ((Binding)date.Binding).StringFormat = "yyyy-MM-dd HH:mm";
            var candidates = row.Candidates.Select(file => new CandidateRow(file, row.MatchReason(file))).ToList();
            var table = Dialog.Table(candidates, Dialog.Column("文件名", "Name", 202),
                Dialog.Column("匹配依据", "Reason", 154), date,
                Dialog.Column("完整来源路径", "Path", 1, true));
            dialog.ShowContent(table);
            Func<ArchiveEntry> selected = () =>
            {
                var choice = table.SelectedItem as CandidateRow;
                return choice == null ? null : choice.File;
            };
            var open = dialog.AddButton("打开文件", () => openFile(selected(), dialog));
            dialog.AddClose("取消");
            ArchiveEntry chosen = null;
            var confirm = dialog.AddButton("选用这个文件", () => { chosen = selected(); dialog.Close(); }, true);
            Action update = () => open.IsEnabled = confirm.IsEnabled = table.SelectedItem != null;
            table.SelectionChanged += (sender, args) => update();
            table.SelectedIndex = row.Chosen == null ? -1 : row.Candidates.IndexOf(row.Chosen);
            update();
            dialog.ShowDialog();
            return chosen;
        }

        private sealed class CandidateRow
        {
            public ArchiveEntry File { get; private set; }
            public string Reason { get; private set; }
            public string Name { get { return File.Name; } }
            public string Path { get { return File.Path; } }
            public DateTime? Modified { get { return File.Modified; } }

            public CandidateRow(ArchiveEntry file, string reason)
            {
                File = file;
                Reason = reason;
            }
        }

        private sealed class Option : INotifyPropertyChanged
        {
            private bool isChecked;
            public string Label { get; private set; }
            public ArchiveEntry Folder { get; private set; }
            public Option(string label, ArchiveEntry folder = null) { Label = label; Folder = folder; }
            public bool Checked
            {
                get { return isChecked; }
                set
                {
                    if (isChecked == value) return;
                    isChecked = value;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Checked"));
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
