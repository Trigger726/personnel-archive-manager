using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArchiveManager
{
    internal sealed class Workspace : Window
    {
        private static readonly Geometry FolderIcon = MakeIcon("M 1,4 L 7,4 L 9,6 L 15,6 L 15,13 L 1,13 Z M 1,7 L 15,7");
        private static readonly Geometry DocumentIcon = MakeIcon("M 3,1 L 10,1 L 14,5 L 14,15 L 3,15 Z M 10,1 L 10,5 L 14,5 M 6,8 L 11,8 M 6,11 L 11,11");
        private static readonly Geometry SearchIcon = MakeIcon("M 11,6 A 5,5 0 1 1 1,6 A 5,5 0 1 1 11,6 M 10,10 L 15,15");
        private static readonly string[] MaterialKeywords =
        {
            "入职协议", "劳动合同", "入职登记表", "保密协议", "转正审批表", "岗位职责确认书",
            "岗位调整通知书", "培训签到记录", "绩效考核记录", "考勤异常说明", "档案移交清单", "入职材料签收单"
        };
        private readonly Grid surface;
        private readonly DataGrid filesGrid;
        private readonly TextBox searchBox;
        private readonly TreeView tree;
        private readonly ComboBox scope;
        private readonly ProgressBar progressBar;
        private readonly Border copyDropZone;
        private ILookup<string, ArchiveEntry> employeeFiles = Enumerable.Empty<ArchiveEntry>()
            .ToLookup(file => file.EmployeePath, StringComparer.OrdinalIgnoreCase);
        private const string CopyDragFormat = "ArchiveManager.CopySelection";
        private const string DropHint = "拖入文件或员工节点";
        private string dragToken;
        private ArchiveEntry[] draggedFiles;
        private DirectoryNode activeDirectory;
        private readonly Dictionary<string, ArchiveEntry> selection = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> selectionIssues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> departments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> employees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ArchiveSettings settings = new ArchiveSettings();
        private ArchiveIndex index = new ArchiveIndex();
        private List<FileRow> visibleRows = new List<FileRow>();
        private string folder;
        private string query = "";
        private string lastDestination;
        private DateTime? lastRefresh;
        private bool scanning;
        private bool copying;
        private bool rebuildingTree;
        private bool suppressSelectionStatus;
        private SearchSnapshot clearedSearch;
        private bool playedIntro;

        private sealed class SearchSnapshot
        {
            public string Query, Draft;
            public int Scope;
            public string[][] Filters;
        }

        public Workspace()
        {
            Title = "人事档案管理";
            Width = Math.Min(1240, SystemParameters.WorkArea.Width);
            Height = Math.Min(760, SystemParameters.WorkArea.Height);
            MinWidth = 870;
            MinHeight = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Appearance.InterfaceFont(AppDomain.CurrentDomain.BaseDirectory);
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "Ink");
            UseLayoutRounding = true;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Workspace.xaml"))
                surface = (Grid)XamlReader.Load(stream);
            Content = surface;
            filesGrid = Find<DataGrid>("FilesGrid");
            searchBox = Find<TextBox>("SearchBox");
            tree = Find<TreeView>("DirectoryTree");
            tree.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(AnimateDirectoryExpansion));
            scope = Find<ComboBox>("ScopeBox");
            progressBar = Find<ProgressBar>("Progress");
            copyDropZone = Find<Border>("CopyDropZone");
            BindDragSource(filesGrid, source =>
            {
                if (Ancestor<CheckBox>(source) != null) return null;
                var row = Ancestor<DataGridRow>(source);
                var file = row == null ? null : row.Item as FileRow;
                return file != null && !file.IsFolder ? file.Entry : null;
            });
            BindDragSource(tree, source =>
            {
                var item = Ancestor<TreeViewItem>(source);
                if (item == null || Ancestor<ToggleButton>(source) != null) return null;
                var header = item.Template == null ? null : item.Template.FindName("Node", item) as Border;
                var node = item.DataContext as DirectoryNode;
                return header != null && header.IsMouseOver && node != null && node.FileCount > 0 ? node.Entry : null;
            });
            copyDropZone.DragEnter += UpdateDropTarget;
            copyDropZone.DragOver += UpdateDropTarget;
            copyDropZone.DragLeave += (sender, args) =>
            {
                var bounds = new Rect(copyDropZone.RenderSize);
                if (!bounds.Contains(args.GetPosition(copyDropZone))) SetDropHighlight(false);
            };
            copyDropZone.Drop += (sender, args) =>
            {
                args.Handled = true;
                args.Effects = DragDropEffects.None;
                SetDropHighlight(false);
                if (!CanAcceptDrop(args)) return;
                AddToSelection(draggedFiles);
                args.Effects = DragDropEffects.Copy;
            };
            // Keep the user's title animation on launch and pointer hover.
            var title = Find<TextBlock>("BrandTitle");
            var cyan = Find<TextBlock>("TitleCyan");
            var rose = Find<TextBlock>("TitleRose");
            Find<DockPanel>("Brand").MouseEnter += (sender, args) => Appearance.GlitchTitle(title, cyan, rose);
            ContentRendered += (sender, args) =>
            {
                if (playedIntro) return;
                playedIntro = true;
                Appearance.GlitchTitle(title, cyan, rose);
            };
            var identityColumns = filesGrid.Columns.Where(column => Equals(column.Header, "员工") || Equals(column.Header, "部门")).ToArray();
            var nameColumn = filesGrid.Columns.Single(column => column.SortMemberPath == "Name");
            var archiveBody = Find<Grid>("ArchiveBody");
            var directoryColumn = archiveBody.ColumnDefinitions[0];
            archiveBody.SizeChanged += (sender, args) =>
            {
                if (!args.WidthChanged) return;
                double available = args.NewSize.Width - archiveBody.ColumnDefinitions[1].Width.Value - archiveBody.ColumnDefinitions[2].MinWidth;
                directoryColumn.MaxWidth = Math.Max(directoryColumn.MinWidth, Math.Min(340, available));
            };
            Find<FrameworkElement>("WorkArea").SizeChanged += (sender, args) =>
            {
                bool compact = args.NewSize.Width < 790;
                foreach (var column in identityColumns)
                    column.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                if (!args.WidthChanged) return;
                // Leave room for the vertical scrollbar when sizing the filename in a narrow window.
                nameColumn.MinWidth = Math.Max(225, Math.Floor(args.NewSize.Width - 44 - 40 - 61 - 140 - 18 - (compact ? 0 : 201)));
            };

            Bind("SettingsButton", ShowSettings);
            Bind("ChooseRootButton", ShowSettings);
            Bind("CollectButton", CollectMaterials);
            Find<Button>("RefreshButton").Click += async (sender, args) => await RefreshArchive();
            Bind("SearchButton", ApplySearch);
            Bind("ResetButton", ResetSearch);
            Bind("RestoreButton", RestoreSearch);
            Bind("DepartmentButton", () => ShowFilter("部门", departments, index.Files.Where(file => file.Department.Length > 0)
                .Select(file => new FilterOption(file.Department, file.Department))));
            Bind("EmployeeButton", () => ShowFilter("员工", employees, index.Files.Where(file => file.EmployeePath.Length > 0)
                .Select(file => new FilterOption(file.EmployeePath, file.Employee + "　·　" + file.Department + "　·　" + file.EmployeePath))));
            Bind("FormatButton", () => ShowFilter("格式", formats, index.Files.Select(file => new FilterOption(file.Format, file.Format))));
            Bind("KeywordButton", () => ShowFilter("资料关键词", keywords,
                MaterialKeywords.Select(word => new FilterOption(word, word)),
                "按文件名匹配。选择多项时，命中任一关键词即可。", true));
            Bind("UpButton", GoUp);
            Bind("SelectResultsButton", SelectResults);
            Bind("SelectedButton", ShowSelection);
            Bind("ClearSelectedButton", ClearSelection);
            Bind("ProblemsButton", ShowProblems);
            Find<Button>("CopyButton").Click += async (sender, args) => await CopySelection();
            searchBox.TextChanged += (sender, args) => UpdateResetButton();
            scope.SelectionChanged += (sender, args) => UpdateView();
            tree.SelectedItemChanged += (sender, args) =>
            {
                var item = args.NewValue as TreeViewItem;
                SetActiveDirectory(item == null ? null : item.DataContext as DirectoryNode);
                if (rebuildingTree || item == null || !(item.Tag is string)) return;
                folder = (string)item.Tag;
                UpdateView();
            };
            filesGrid.MouseDoubleClick += (sender, args) =>
            {
                var source = args.OriginalSource as DependencyObject;
                var row = Ancestor<DataGridRow>(source);
                if (row != null && Ancestor<CheckBox>(source) == null) OpenEntry(((FileRow)row.Item).Entry);
            };
            filesGrid.PreviewMouseRightButtonDown += (sender, args) =>
            {
                var row = Ancestor<DataGridRow>(args.OriginalSource as DependencyObject);
                filesGrid.SelectedItem = row == null ? null : row.Item;
            };
            filesGrid.ContextMenu = new ContextMenu();
            var addFile = new MenuItem { Header = "加入待复制" };
            addFile.Click += (sender, args) =>
            {
                var row = filesGrid.SelectedItem as FileRow;
                if (row != null && !row.IsFolder && !scanning && !copying) AddToSelection(new[] { row.Entry });
            };
            var locate = new MenuItem { Header = "打开所在文件夹" };
            locate.Click += (sender, args) =>
            {
                var row = filesGrid.SelectedItem as FileRow;
                if (row != null) Launch("explorer.exe", "/select,\"" + row.Entry.Path + "\"");
            };
            filesGrid.ContextMenu.Items.Add(addFile);
            filesGrid.ContextMenu.Items.Add(locate);
            filesGrid.ContextMenuOpening += (sender, args) =>
            {
                var row = filesGrid.SelectedItem as FileRow;
                args.Handled = row == null;
                addFile.IsEnabled = row != null && !row.IsFolder && !row.Checked;
            };

            PreviewKeyDown += async (sender, args) =>
            {
                if (copying || scanning) return;
                if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    searchBox.Focus();
                    searchBox.SelectAll();
                    args.Handled = true;
                }
                else if (args.Key == Key.Escape && searchBox.IsKeyboardFocusWithin)
                {
                    ResetSearch();
                    args.Handled = true;
                }
                else if (args.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && filesGrid.IsKeyboardFocusWithin)
                {
                    SelectResults();
                    args.Handled = true;
                }
                else if (args.Key == Key.Space && filesGrid.IsKeyboardFocusWithin && !(Keyboard.FocusedElement is CheckBox))
                {
                    var row = filesGrid.SelectedItem as FileRow;
                    if (row != null && !row.IsFolder) row.Checked = !row.Checked;
                    args.Handled = true;
                }
                else if (args.Key == Key.F5)
                {
                    args.Handled = true;
                    await RefreshArchive();
                }
                else if (args.Key == Key.Enter && searchBox.IsKeyboardFocusWithin)
                {
                    args.Handled = true;
                    ApplySearch();
                }
                else if (args.Key == Key.Enter && filesGrid.IsKeyboardFocusWithin && !(Keyboard.FocusedElement is CheckBox))
                {
                    var row = filesGrid.SelectedItem as FileRow;
                    if (row == null) return;
                    args.Handled = true;
                    OpenEntry(row.Entry);
                }
            };
            Closing += (sender, args) => { if (copying) args.Cancel = true; };
            Loaded += async (sender, args) =>
            {
                try { settings = ArchiveSettings.Load(); }
                catch (Exception error)
                {
                    if (!ArchiveFiles.IsFileError(error)) throw;
                    ShowError("无法读取设置", error);
                }
                if (settings.Root.Length > 0) await RefreshArchive();
            };
            UpdateView();
        }

        private T Find<T>(string name) where T : FrameworkElement { return (T)surface.FindName(name); }
        private void Bind(string name, Action action) { Find<Button>(name).Click += (sender, args) => action(); }

        private static Geometry MakeIcon(string drawing)
        {
            var geometry = Geometry.Parse(drawing);
            geometry.Freeze();
            return geometry;
        }

        private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
        {
            while (element != null)
            {
                var match = element as T;
                if (match != null) return match;
                var content = element as FrameworkContentElement;
                element = content != null ? content.Parent :
                    element is Visual || element is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : null;
            }
            return null;
        }

        private void BindDragSource(UIElement source, Func<DependencyObject, ArchiveEntry> hitTest)
        {
            ArchiveEntry candidate = null;
            Point start = new Point();
            source.PreviewMouseLeftButtonDown += (sender, args) =>
            {
                candidate = args.ClickCount == 1 ? hitTest(args.OriginalSource as DependencyObject) : null;
                start = args.GetPosition(surface);
            };
            source.PreviewMouseLeftButtonUp += (sender, args) => candidate = null;
            source.MouseLeave += (sender, args) => candidate = null;
            source.PreviewMouseMove += (sender, args) =>
            {
                if (args.LeftButton != MouseButtonState.Pressed) { candidate = null; return; }
                if (candidate == null || scanning || copying || dragToken != null) return;
                Vector delta = args.GetPosition(surface) - start;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                var entry = candidate;
                candidate = null;
                BeginCopyDrag(source, entry);
                args.Handled = true;
            };
        }

        private IEnumerable<ArchiveEntry> CopyDragFiles(ArchiveEntry entry)
        {
            if (entry.IsEmployeeFolder) return employeeFiles[entry.EmployeePath];
            if (entry.IsFolder) return Enumerable.Empty<ArchiveEntry>();
            return selection.ContainsKey(entry.Path) ? selection.Values.AsEnumerable() : new[] { entry };
        }

        private void BeginCopyDrag(UIElement source, ArchiveEntry entry)
        {
            draggedFiles = CopyDragFiles(entry).ToArray();
            if (draggedFiles.Length == 0) return;
            dragToken = Guid.NewGuid().ToString("N");
            try
            {
                SetDropHighlight(false);
                DragDrop.DoDragDrop(source, new DataObject(CopyDragFormat, dragToken, false), DragDropEffects.Copy);
            }
            finally
            {
                dragToken = null;
                draggedFiles = null;
                SetDropHighlight(false);
            }
        }

        private bool CanAcceptDrop(DragEventArgs args)
        {
            return CanAcceptDrop(args.Data, args.AllowedEffects);
        }

        private bool CanAcceptDrop(IDataObject data, DragDropEffects effects)
        {
            return dragToken != null && !scanning && !copying &&
                (effects & DragDropEffects.Copy) != 0 && data.GetDataPresent(CopyDragFormat, false) &&
                string.Equals(dragToken, data.GetData(CopyDragFormat, false) as string, StringComparison.Ordinal);
        }

        private void UpdateDropTarget(object sender, DragEventArgs args)
        {
            bool allowed = CanAcceptDrop(args);
            SetDropHighlight(allowed);
            args.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        }

        private void SetDropHighlight(bool overTarget)
        {
            copyDropZone.Tag = overTarget;
            Find<System.Windows.Shapes.Rectangle>("DropOutline").Visibility = dragToken == null ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("DropHint").Text = dragToken == null ? DropHint :
                (overTarget ? "松开，加入 " : "拖到这里，加入 ") + draggedFiles.Length + " 份文件";
        }

        private void ShowSettings()
        {
            if (scanning || copying) return;
            var dialog = new Dialog(this, "档案设置", 615, 338, "从档案根目录往下计算，第一级是根目录下的第一层文件夹。");
            var form = new StackPanel();
            var pathLine = new DockPanel { Margin = new Thickness(0, 4, 0, 12) };
            var path = new TextBox { Text = settings.Root };
            AutomationProperties.SetName(path, "档案根目录");
            var choose = Program.Button("浏览…", () =>
            {
                string chosen = PickFolder(dialog, "选择档案根目录", path.Text.Trim());
                if (chosen != null) path.Text = chosen;
            });
            DockPanel.SetDock(choose, Dock.Right);
            pathLine.Children.Add(choose);
            pathLine.Children.Add(path);
            form.Children.Add(new TextBlock { Text = "档案根目录" });
            form.Children.Add(pathLine);
            var levels = new StackPanel { Orientation = Orientation.Horizontal };
            var department = AddLevel(levels, "部门层级", settings.DepartmentLevel);
            var employee = AddLevel(levels, "员工层级", settings.EmployeeLevel);
            form.Children.Add(levels);
            form.Children.Add(new TextBlock
            {
                Text = "最近刷新　" + (lastRefresh.HasValue ? lastRefresh.Value.ToString("yyyy-MM-dd HH:mm:ss") : "本次启动尚未刷新"),
                Foreground = Program.Brush("#54647A"), Margin = new Thickness(0, 13, 0, 0)
            });
            var validation = new TextBlock { Foreground = Program.Brush("#A22D28"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            form.Children.Add(validation);
            dialog.ShowContent(form);
            dialog.AddClose("取消");
            dialog.AddButton("保存并刷新", () =>
            {
                int departmentLevel, employeeLevel;
                if (!int.TryParse(department.Text, out departmentLevel) || !int.TryParse(employee.Text, out employeeLevel) ||
                    !ArchiveSettings.ValidLevels(departmentLevel, employeeLevel))
                {
                    validation.Text = "请输入 1–30 的整数，员工层级应大于部门层级。";
                    return;
                }
                try
                {
                    string entered = path.Text.Trim().Trim('"');
                    if (!Path.IsPathRooted(entered) || !Directory.Exists(entered))
                    {
                        validation.Text = "请选择可访问的档案根目录，或输入完整的文件夹路径。";
                        return;
                    }
                    string normalized = Path.GetFullPath(entered);
                    if (normalized.Length > Path.GetPathRoot(normalized).Length) normalized = normalized.TrimEnd('\\', '/');
                    var candidate = new ArchiveSettings { Root = normalized, DepartmentLevel = departmentLevel, EmployeeLevel = employeeLevel };
                    candidate.Save();
                    dialog.Tag = candidate;
                    dialog.Close();
                }
                catch (Exception error)
                {
                    if (!ArchiveFiles.IsFileError(error)) throw;
                    validation.Text = ArchiveFiles.ErrorText(error);
                }
            }, true).IsDefault = true;
            dialog.ShowDialog();
            var updated = dialog.Tag as ArchiveSettings;
            if (updated != null) SetArchive(updated);
        }

        private static TextBox AddLevel(Panel panel, string label, int value)
        {
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var input = new TextBox { Text = value.ToString(), Width = 54, Margin = new Thickness(0, 0, 22, 0) };
            AutomationProperties.SetName(input, label);
            panel.Children.Add(input);
            return input;
        }

        private async void SetArchive(ArchiveSettings updated)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(settings.Root, updated.Root))
            {
                folder = updated.Root;
                index = new ArchiveIndex();
                employeeFiles = Enumerable.Empty<ArchiveEntry>().ToLookup(file => file.EmployeePath, StringComparer.OrdinalIgnoreCase);
                selection.Clear();
                selectionIssues.Clear();
                ResetSearch();
                clearedSearch = null;
            }
            else if (settings.DepartmentLevel != updated.DepartmentLevel || settings.EmployeeLevel != updated.EmployeeLevel)
            {
                departments.Clear();
                employees.Clear();
                clearedSearch = null;
            }
            settings = updated;
            await RefreshArchive();
        }

        private async Task RefreshArchive()
        {
            if (scanning || copying || settings.Root.Length == 0) return;
            scanning = true;
            var timer = Stopwatch.StartNew();
            SetProgress("正在读取档案目录…", true);
            UpdateControls();
            var report = new Progress<int>(count => { if (scanning) SetStatus("正在刷新，已读取 " + count + " 份文件…"); });
            try
            {
                var selected = selection.Values.ToArray();
                var refreshed = await Task.Run(() =>
                {
                    var scanned = ArchiveIndex.Scan(settings, count => ((IProgress<int>)report).Report(count));
                    return Tuple.Create(scanned, scanned.CheckSelectionAfterScan(selected));
                });
                index = refreshed.Item1;
                selectionIssues = refreshed.Item2;
                lastRefresh = DateTime.Now;
                foreach (var file in index.Files)
                    if (selection.ContainsKey(file.Path)) selection[file.Path] = file;
                if (folder == null || !index.Contents.ContainsKey(folder)) folder = settings.Root;
                RebuildTree();
                SetStatus("已索引 " + index.Files.Count + " 份文件，刷新用时 " + timer.Elapsed.TotalSeconds.ToString("0.0") + " 秒");
            }
            catch (Exception error) { ShowError("刷新失败", error); SetStatus("刷新失败，可重试。"); }
            finally
            {
                scanning = false;
                progressBar.Visibility = Visibility.Collapsed;
                UpdateView();
            }
        }

        private void RebuildTree()
        {
            employeeFiles = index.Files.Where(file => file.EmployeePath.Length > 0)
                .ToLookup(file => file.EmployeePath, StringComparer.OrdinalIgnoreCase);
            rebuildingTree = true;
            try
            {
                SetActiveDirectory(null);
                tree.Items.Clear();
                var root = CreateNode("档案根目录", settings.Root);
                tree.Items.Add(root);
                root.IsExpanded = true;
                Navigate(folder);
            }
            finally { rebuildingTree = false; }
        }

        private void SetActiveDirectory(DirectoryNode node)
        {
            if (ReferenceEquals(activeDirectory, node)) return;
            for (var cursor = activeDirectory; cursor != null; cursor = cursor.Parent) cursor.IsOnPath = false;
            activeDirectory = node;
            for (var cursor = node; cursor != null; cursor = cursor.Parent) cursor.IsOnPath = true;
        }

        private void AnimateDirectoryExpansion(object sender, RoutedEventArgs args)
        {
            var expander = args.OriginalSource as ToggleButton;
            if (expander == null || expander.Name != "Expander" || expander.IsChecked != true) return;
            var node = expander.TemplatedParent as TreeViewItem;
            if (node == null || !node.IsExpanded) return;

            // Animate at most the rows that fit in the directory pane.
            int visibleRows = (int)Math.Ceiling(tree.ActualHeight / 38) + 1;
            Appearance.RevealDirectoryChildren(node.Items.OfType<TreeViewItem>()
                .Take(visibleRows).Select(child => child.Header).OfType<FrameworkElement>());
        }

        private sealed class DirectoryNode : INotifyPropertyChanged
        {
            public ArchiveEntry Entry { get; private set; }
            public DirectoryNode Parent { get; private set; }
            public bool IsRoot { get { return Entry == null; } }
            public bool IsLast { get; private set; }
            public int FileCount { get; private set; }
            private bool isOnPath;
            public bool IsOnPath
            {
                get { return isOnPath; }
                set
                {
                    if (isOnPath == value) return;
                    isOnPath = value;
                    var handler = PropertyChanged;
                    if (handler != null) handler(this, new PropertyChangedEventArgs("IsOnPath"));
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;

            public DirectoryNode(ArchiveEntry entry, DirectoryNode parent, bool isLast, int fileCount)
            {
                Entry = entry;
                Parent = parent;
                IsLast = isLast;
                FileCount = fileCount;
            }
        }

        private TreeViewItem CreateNode(string name, string path, ArchiveEntry entry = null, DirectoryNode parent = null, bool isLast = false)
        {
            int count = entry != null && entry.IsEmployeeFolder ? employeeFiles[entry.EmployeePath].Count() : 0;
            var data = new DirectoryNode(entry, parent, isLast, count);
            var header = new DockPanel { Background = Brushes.Transparent };
            if (entry != null && entry.IsEmployeeFolder)
            {
                var badge = new TextBlock { Text = count.ToString(), FontSize = 11, FontWeight = FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 1, 0) };
                badge.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                DockPanel.SetDock(badge, Dock.Right);
                header.Children.Add(badge);
            }
            var icon = new System.Windows.Shapes.Path
            {
                Data = FolderIcon, Stroke = Program.Brush("#3975B9"), StrokeThickness = 1.2, Stretch = Stretch.Uniform,
                Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
            };
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(icon);
            header.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            var node = new TreeViewItem { Header = header, Tag = path, ToolTip = path, DataContext = data };
            AutomationProperties.SetName(node, name);
            if (count > 0)
            {
                header.Cursor = Cursors.Hand;
                ToolTipService.SetToolTip(header, path + "\n拖到待复制区，加入 " + count + " 份档案");
                var add = new MenuItem { Header = "加入该员工的 " + count + " 份档案" };
                add.Click += (sender, args) =>
                {
                    if (!scanning && !copying) AddToSelection(employeeFiles[entry.EmployeePath]);
                };
                node.ContextMenu = new ContextMenu();
                node.ContextMenu.Items.Add(add);
                AutomationProperties.SetHelpText(node, "含 " + count + " 份档案；可拖入待复制区，或从右键菜单加入");
            }
            List<ArchiveEntry> entries;
            if (index.Contents.TryGetValue(path, out entries) && entries.Any(item => item.IsFolder))
            {
                node.Items.Add(new TreeViewItem());
                node.Expanded += (sender, args) =>
                {
                    if (!ReferenceEquals(args.OriginalSource, node) || node.Items.Count != 1 || ((TreeViewItem)node.Items[0]).Tag != null) return;
                    node.Items.Clear();
                    var children = entries.Where(file => file.IsFolder).OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                    for (int i = 0; i < children.Count; i++)
                    {
                        var child = children[i];
                        node.Items.Add(CreateNode(child.Name, child.Path, child, data, i == children.Count - 1));
                    }
                };
            }
            return node;
        }

        private void Navigate(string path)
        {
            if (tree.Items.Count == 0) return;
            var current = (TreeViewItem)tree.Items[0];
            if (!StringComparer.OrdinalIgnoreCase.Equals(path, settings.Root) && ArchiveFiles.IsWithin(path, settings.Root))
            {
                var levels = path.Substring(settings.Root.TrimEnd('\\').Length + 1).Split('\\');
                string next = settings.Root;
                foreach (var level in levels)
                {
                    next = Path.Combine(next, level);
                    current.IsExpanded = true;
                    var child = current.Items.OfType<TreeViewItem>().FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals((string)item.Tag, next));
                    if (child == null) break;
                    current = child;
                }
            }
            current.IsSelected = true;
            SetActiveDirectory(current.DataContext as DirectoryNode);
            current.BringIntoView();
        }

        private void GoUp()
        {
            if (folder != null && !StringComparer.OrdinalIgnoreCase.Equals(folder, settings.Root)) Navigate(Path.GetDirectoryName(folder));
        }

        private bool IsSearch { get { return query.Length > 0 || departments.Count > 0 || employees.Count > 0 || formats.Count > 0 || keywords.Count > 0; } }

        private void ApplySearch()
        {
            query = searchBox.Text.Trim();
            UpdateView();
        }

        private void RestoreSearch()
        {
            if (clearedSearch == null) return;
            var saved = clearedSearch;
            clearedSearch = null;
            query = saved.Query;
            searchBox.Text = saved.Draft;
            var filters = new[] { departments, employees, formats, keywords };
            for (int i = 0; i < filters.Length; i++)
            {
                filters[i].Clear();
                filters[i].UnionWith(saved.Filters[i]);
            }
            scope.SelectedIndex = saved.Scope;
            UpdateView();
        }

        private void ResetSearch()
        {
            if (IsSearch || searchBox.Text.Length > 0)
                clearedSearch = new SearchSnapshot { Query = query, Draft = searchBox.Text, Scope = scope.SelectedIndex,
                    Filters = new[] { departments, employees, formats, keywords }.Select(values => values.ToArray()).ToArray() };
            searchBox.Clear();
            query = "";
            departments.Clear();
            employees.Clear();
            formats.Clear();
            keywords.Clear();
            UpdateView();
        }

        private void UpdateView()
        {
            List<ArchiveEntry> entries;
            IEnumerable<ArchiveEntry> results = Enumerable.Empty<ArchiveEntry>();
            if (IsSearch)
                results = index.Search(scope.SelectedIndex == 1 ? folder : null, query, departments, employees, formats, keywords);
            else if (folder != null && index.Contents.TryGetValue(folder, out entries))
                results = entries;
            visibleRows = results.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => new FileRow(this, entry)).ToList();
            var sort = CollectionViewSource.GetDefaultView(filesGrid.ItemsSource);
            var savedSort = sort == null ? new SortDescription[0] : sort.SortDescriptions.ToArray();
            filesGrid.ItemsSource = visibleRows;

            var currentSort = CollectionViewSource.GetDefaultView(visibleRows);
            using (currentSort.DeferRefresh())
                foreach (var order in savedSort) currentSort.SortDescriptions.Add(order);
            foreach (var column in filesGrid.Columns)
            {
                var order = savedSort.FirstOrDefault(item => item.PropertyName == column.SortMemberPath);
                column.SortDirection = order.PropertyName == null ? (ListSortDirection?)null : order.Direction;
            }

            int fileCount = visibleRows.Count(row => !row.IsFolder);
            int folderCount = visibleRows.Count - fileCount;
            var rootLabel = Find<TextBlock>("RootLabel");
            bool unconfigured = settings.Root.Length == 0;
            rootLabel.Text = unconfigured ? "尚未选择目录" : FolderName(settings.Root);
            rootLabel.ToolTip = unconfigured ? rootLabel.Text : settings.Root;
            Find<TextBlock>("ViewTitle").Text = IsSearch ? "搜索结果" : folder == null ? "档案文件" :
                StringComparer.OrdinalIgnoreCase.Equals(folder, settings.Root) ? "档案根目录" : FolderName(folder);
            var location = Find<TextBlock>("LocationLabel");
            string locationText = IsSearch && scope.SelectedIndex == 0 ? "全部档案" : folder == null ? "" :
                StringComparer.OrdinalIgnoreCase.Equals(folder, settings.Root) ? FolderName(settings.Root) : RelativeFolder(folder);
            string totals = fileCount + " 份文件" + (folderCount == 0 ? "" : "，" + folderCount + " 个文件夹");
            location.Text = unconfigured ? "" : locationText + "　/　" + totals;
            location.ToolTip = IsSearch && scope.SelectedIndex == 0 ? location.Text : folder;
            Find<Button>("SelectResultsButton").Content = "全选当前结果（" + fileCount + "）";
            Find<StackPanel>("EmptyPanel").Visibility = visibleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool unreadable = folder != null && index.Problems.Any(problem => problem.StartsWith(folder + "　", StringComparison.OrdinalIgnoreCase));
            Find<System.Windows.Shapes.Path>("EmptyIcon").Data = IsSearch ? SearchIcon : FolderIcon;
            tree.Visibility = unconfigured ? Visibility.Hidden : Visibility.Visible;
            Find<TextBlock>("EmptyTitle").Text = unconfigured ? "连接现有档案目录" : IsSearch ? "未找到匹配文件" : unreadable ? "此目录未能读取" : "此文件夹为空";
            Find<TextBlock>("EmptyMessage").Text = unconfigured ? "选择存放部门及员工文件夹的根目录，即可浏览和搜索档案。" :
                IsSearch ? "可修改关键词、清空筛选或刷新。" : unreadable ? "请查看读取问题中的路径和原因。" : "目录更新后，点击刷新查看最新内容。";
            Find<Button>("ChooseRootButton").Visibility = unconfigured ? Visibility.Visible : Visibility.Collapsed;
            if (unconfigured) SetStatus("请选择档案根目录");
            if (index.Problems.Count > 0) SetStatus("部分目录或文件未能读取，结果可能不完整。");
            Find<Button>("ProblemsButton").Visibility = index.Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateControls();
        }

        private void UpdateControls()
        {
            bool available = !scanning && !copying;
            bool hasRoot = settings.Root.Length > 0;
            foreach (var name in new[] { "SearchBox", "SearchButton", "ResetButton", "DepartmentButton", "EmployeeButton", "FormatButton", "KeywordButton", "ScopeBox", "RefreshButton", "CollectButton", "DirectoryTree", "FilesGrid" })
                Find<FrameworkElement>(name).IsEnabled = available && hasRoot;
            Find<Button>("SettingsButton").IsEnabled = available;
            Find<Button>("UpButton").IsEnabled = available && folder != null && !StringComparer.OrdinalIgnoreCase.Equals(folder, settings.Root);
            Find<Button>("SelectResultsButton").IsEnabled = available && visibleRows.Any(row => !row.IsFolder);
            UpdateResetButton();
            UpdateFilterButton("DepartmentButton", "部门", departments);
            UpdateFilterButton("EmployeeButton", "员工", employees);
            UpdateFilterButton("FormatButton", "格式", formats);
            UpdateFilterButton("KeywordButton", "资料关键词", keywords);
            Find<Button>("KeywordButton").ToolTip = keywords.Count == 0 ? "按入职协议、劳动合同等文件名关键词筛选" :
                string.Join("、", MaterialKeywords.Where(keywords.Contains));
            var summary = new List<string>();
            if (query.Length > 0) summary.Add("搜索：" + query);
            var groups = new[] { departments, employees, formats, keywords };
            var labels = new[] { "部门", "员工", "格式", "资料" };
            for (int i = 0; i < groups.Length; i++)
                if (groups[i].Count > 0) summary.Add(labels[i] + "：" + string.Join("、", groups[i].OrderBy(value => value)));
            var applied = Find<TextBlock>("AppliedFilters");
            applied.Text = string.Join("；", summary);
            applied.ToolTip = applied.Text;
            Find<Border>("SearchSummary").Visibility = summary.Count > 0 || clearedSearch != null ? Visibility.Visible : Visibility.Collapsed;
            var restore = Find<Button>("RestoreButton");
            restore.Visibility = clearedSearch != null ? Visibility.Visible : Visibility.Collapsed;
            restore.IsEnabled = available && hasRoot;
            UpdateSelectionStatus();
        }

        private void UpdateFilterButton(string name, string label, HashSet<string> values)
        {
            var button = Find<Button>(name);
            button.ToolTip = values.Count == 0 ? "选择" + label : string.Join("、", values.OrderBy(value => value));
            button.Tag = values.Count > 0;
            button.Content = values.Count == 0 ? label + " ▾" : label + " " + values.Count + " ▾";
        }

        private void UpdateResetButton()
        {
            Find<Button>("ResetButton").IsEnabled = !scanning && !copying && settings.Root.Length > 0 &&
                (IsSearch || searchBox.Text.Length > 0);
        }

        private static string FolderName(string path)
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            return name.Length > 0 ? name : path;
        }

        private string RelativeFolder(string path)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(path, settings.Root)) return "档案根目录";
            return ArchiveFiles.IsWithin(path, settings.Root) ? path.Substring(settings.Root.TrimEnd('\\').Length + 1) : path;
        }

        private void ShowFilter(string label, HashSet<string> values, IEnumerable<FilterOption> options,
            string description = null, bool keepOrder = false)
        {
            var unique = options.GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
            var all = (keepOrder ? unique : unique.OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)).ToList();
            foreach (var option in all) option.Checked = values.Contains(option.Value);
            var dialog = new Dialog(this, label + "筛选", 570, 470, description ?? ("勾选要包含在搜索结果中的" + label + "。"));
            var body = new DockPanel();
            var filter = new TextBox { Margin = new Thickness(0, 0, 0, 9), ToolTip = "搜索" + label };
            AutomationProperties.SetName(filter, "搜索" + label);
            DockPanel.SetDock(filter, Dock.Top);
            body.Children.Add(filter);
            var list = new ListBox { ItemsSource = all };
            list.SetResourceReference(Control.BorderBrushProperty, "Rule");
            var template = new DataTemplate();
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ContentControl.ContentProperty, new Binding("Label"));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding("Checked") { Mode = BindingMode.TwoWay });
            checkBox.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 4, 4, 4));
            template.VisualTree = checkBox;
            list.ItemTemplate = template;
            body.Children.Add(list);
            dialog.ShowContent(body);
            filter.TextChanged += (sender, args) => list.ItemsSource = all.Where(item => item.Label.IndexOf(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            dialog.AddButton("清空筛选", () => { foreach (var option in all) option.Checked = false; });
            dialog.AddClose("取消");
            dialog.AddButton("应用筛选", () =>
            {
                values.Clear();
                values.UnionWith(all.Where(option => option.Checked).Select(option => option.Value));
                dialog.Close();
                ApplySearch();
            }, true);
            dialog.Loaded += (sender, args) => filter.Focus();
            dialog.ShowDialog();
        }

        private void UpdateSelectionStatus()
        {
            if (suppressSelectionStatus) return;
            var label = Find<TextBlock>("SelectionLabel");
            label.Text = "待复制 " + selection.Count + " 份" +
                (selectionIssues.Count == 0 ? "" : " · " + selectionIssues.Count + " 份需处理");
            label.ToolTip = selectionIssues.Count == 0 ? null : "打开「查看已选」定位或移除无法读取的文件。";
            foreach (var name in new[] { "SelectedButton", "ClearSelectedButton", "CopyButton" })
                Find<Button>(name).IsEnabled = selection.Count > 0 && !copying && !scanning;
        }

        private void CollectMaterials()
        {
            string currentFolder = StringComparer.OrdinalIgnoreCase.Equals(folder, settings.Root) ? null : folder;
            var files = CollectionDialog.Show(this, index, currentFolder, MaterialKeywords,
                (file, owner) => Launch(file.Path, owner: owner));
            if (files.Count > 0) AddToSelection(files);
        }

        private void AddToSelection(IEnumerable<ArchiveEntry> files)
        {
            int before = selection.Count;
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                if (!file.IsFolder && !selection.ContainsKey(file.Path))
                {
                    selection.Add(file.Path, file);
                    added.Add(file.Path);
                }
            foreach (var row in visibleRows)
                if (added.Contains(row.FullPath)) row.NotifyChecked();
            UpdateSelectionStatus();
            SetStatus(selection.Count == before ? "这些文件已在待复制清单中" :
                "本次新增 " + (selection.Count - before) + " 份文件，共 " + selection.Count + " 份待复制");
        }

        private void SelectResults()
        {
            suppressSelectionStatus = true;
            foreach (var row in visibleRows)
                if (!row.IsFolder) row.Checked = true;
            suppressSelectionStatus = false;
            UpdateSelectionStatus();
        }

        private void ClearSelection()
        {
            selection.Clear();
            selectionIssues.Clear();
            foreach (var row in visibleRows) row.NotifyChecked();
            UpdateSelectionStatus();
        }

        private void ShowSelection()
        {
            var rows = new ObservableCollection<SelectionRow>(selection.Values.Select(file => new SelectionRow(file, selectionIssues))
                .OrderByDescending(row => row.HasIssue).ThenBy(row => row.Name));
            var dialog = new Dialog(this, "已选文件", 855, 455, "可定位来源或移除条目。刷新档案后会重新核对文件状态。");
            var table = Dialog.Table(rows, Dialog.Column("文件名", "Name", 212), Dialog.Column("状态", "Status", 218),
                Dialog.Column("完整来源路径", "Path", 1, true));
            dialog.ShowContent(table);
            var clear = dialog.AddButton("清空已选", () => { ClearSelection(); rows.Clear(); });
            var locate = dialog.AddButton("定位来源", async () =>
            {
                var row = table.SelectedItem as SelectionRow;
                if (row == null) return;
                if (!row.HasIssue)
                {
                    LocateSource(row.File, dialog);
                    return;
                }
                string root = settings.Root;
                string nearest = await Task.Run(() => FindReadableParent(row.File.Parent, root));
                if (!dialog.IsVisible) return;
                if (nearest == null)
                    MessageBox.Show(dialog, "当前无法访问档案库。请检查连接或访问权限后重试。", "无法定位来源");
                else
                    Launch("explorer.exe", "\"" + nearest + "\"", dialog);
            });
            var remove = dialog.AddButton("移除所选文件", () =>
            {
                var entry = table.SelectedItem as SelectionRow;
                if (entry == null) return;
                RemoveFromSelection(entry.Path);
                rows.Remove(entry);
                foreach (var row in visibleRows.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.Entry.Path, entry.Path)))
                    row.NotifyChecked();
                UpdateSelectionStatus();
            });
            locate.IsEnabled = remove.IsEnabled = false;
            table.SelectionChanged += (sender, args) => locate.IsEnabled = remove.IsEnabled = table.SelectedItem != null;
            rows.CollectionChanged += (sender, args) => clear.IsEnabled = rows.Count > 0;
            dialog.AddClose();
            dialog.ShowDialog();
        }

        private void RemoveFromSelection(string path)
        {
            selection.Remove(path);
            selectionIssues.Remove(path);
        }

        private void LocateSource(ArchiveEntry file, Window owner)
        {
            Launch("explorer.exe", "/select,\"" + file.Path + "\"", owner);
        }

        private static string FindReadableParent(string directory, string root)
        {
            if (root.Length == 0) return null;
            for (string path = directory; path != null &&
                (StringComparer.OrdinalIgnoreCase.Equals(path, root) || ArchiveFiles.IsWithin(path, root));
                path = Path.GetDirectoryName(path))
            {
                try
                {
                    using (var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) entries.MoveNext();
                    return path;
                }
                catch (Exception error)
                {
                    if (!ArchiveFiles.IsFileError(error)) throw;
                }
            }
            return null;
        }

        private sealed class SelectionRow
        {
            public ArchiveEntry File { get; private set; }
            public string Name { get { return File.Name; } }
            public string Path { get { return File.Path; } }
            public string Status { get; private set; }
            public bool HasIssue { get; private set; }

            public SelectionRow(ArchiveEntry file, Dictionary<string, string> issues)
            {
                File = file;
                string reason;
                HasIssue = issues.TryGetValue(file.Path, out reason);
                Status = HasIssue ? reason : "待复制";
            }
        }

        private void ShowProblems()
        {
            var dialog = new Dialog(this, "读取问题", 815, 430, "部分目录或文件未能读取，结果可能不完整。");
            dialog.ShowContent(Dialog.Table(index.Problems, Dialog.Column("路径及原因", ".", 1, true)));
            dialog.AddClose();
            dialog.ShowDialog();
        }

        private async Task CopySelection()
        {
            if (selection.Count == 0 || copying || scanning) return;
            string root = settings.Root;
            var requested = selection.Values.ToList();
            selectionIssues = await RunCopyCheck("正在核对待复制文件…", () => ArchiveFiles.CheckSources(root, requested, index));
            UpdateSelectionStatus();
            IList<ArchiveEntry> pending = requested.Where(file => !selectionIssues.ContainsKey(file.Path)).ToList();
            int skipped = selectionIssues.Count;
            if (pending.Count == 0)
            {
                SetStatus("没有可复制的文件，请核对待复制清单。");
                ShowSelection();
                return;
            }

            string destination;
            while (true)
            {
                destination = PickFolder(this, "选择档案库之外的目标文件夹", lastDestination);
                if (destination == null) return;
                string selectedFolder = destination;
                string problem = await RunCopyCheck("正在核对复制目标…", () =>
                {
                    try { ArchiveFiles.ValidateOutput(selectedFolder, root); return null as string; }
                    catch (Exception error)
                    {
                        if (!ArchiveFiles.IsFileError(error)) throw;
                        return ArchiveFiles.ErrorText(error);
                    }
                });
                if (problem == null) break;
                MessageBox.Show(this, problem, "无法使用该复制目标", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (!ConfirmCopy(destination, pending, skipped)) return;
            lastDestination = destination;
            string output = null;
            while (pending.Count > 0)
            {
                copying = true;
                SetProgress("正在准备复制 " + pending.Count + " 份文件…", true);
                UpdateControls();
                CopyResult result;
                try
                {
                    output = await Task.Run(() =>
                    {
                        if (output == null) return ArchiveFiles.PrepareOutput(destination, root);
                        ArchiveFiles.ValidateOutput(output, root);
                        return output;
                    });
                    int total = pending.Count;
                    progressBar.Maximum = total;
                    progressBar.IsIndeterminate = false;
                    var report = new Progress<int>(count =>
                    {
                        if (!copying) return;
                        progressBar.Value = count;
                        SetStatus("正在复制　" + count + " / " + total);
                    });
                    result = await Task.Run(() =>
                    {
                        var clock = Stopwatch.StartNew();
                        return ArchiveFiles.Copy(output, pending, count =>
                        {
                            if (count != total && clock.ElapsedMilliseconds < 100) return;
                            ((IProgress<int>)report).Report(count);
                            clock.Restart();
                        });
                    });
                }
                catch (Exception error)
                {
                    if (!ArchiveFiles.IsFileError(error)) throw;
                    result = new CopyResult();
                    result.Failures.AddRange(pending.Select(file => new CopyFailure(file, ArchiveFiles.ErrorText(error))));
                }
                finally
                {
                    copying = false;
                    progressBar.Visibility = Visibility.Collapsed;
                    UpdateControls();
                }
                string summary = "成功 " + result.Successes + " 份，失败 " + result.Failures.Count + " 份" +
                    (skipped == 0 ? "" : "。另有 " + skipped + " 份预检未通过，仍在待复制清单中。");
                SetStatus(summary);
                bool retry = ShowCopyResult(result, output, summary);
                pending = retry ? result.Failures.Select(failure => failure.File).ToList() : new List<ArchiveEntry>();
            }
        }

        private async Task<T> RunCopyCheck<T>(string message, Func<T> check)
        {
            copying = true;
            SetProgress(message, true);
            UpdateControls();
            try { return await Task.Run(check); }
            finally
            {
                copying = false;
                progressBar.Visibility = Visibility.Collapsed;
                UpdateControls();
                SetStatus("");
            }
        }

        private bool ConfirmCopy(string destination, IList<ArchiveEntry> files, int skipped)
        {
            var dialog = new Dialog(this, "确认复制", 760, 480,
                "即将复制 " + files.Count + " 份文件。" + (skipped == 0 ? "" :
                    "另有 " + skipped + " 份无法读取，仍保留在待复制清单中，请通过「查看已选」核对。"));
            var body = new DockPanel();
            var details = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            details.Children.Add(new TextBlock { Text = "目标位置", Margin = new Thickness(0, 0, 0, 6) });
            var path = new TextBox { Text = destination, IsReadOnly = true };
            AutomationProperties.SetName(path, "复制目标位置");
            details.Children.Add(path);
            var rules = new TextBlock { Text = "目标为空时直接复制；非空时新建“档案副本_时间”子目录。\n文件名按“部门_员工_原文件名”生成；重名追加序号，不覆盖已有文件。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            rules.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            details.Children.Add(rules);
            DockPanel.SetDock(details, Dock.Top);
            body.Children.Add(details);
            body.Children.Add(Dialog.Table(files.OrderBy(file => file.Name).ToList(),
                Dialog.Column("文件名", "Name", 260), Dialog.Column("来源路径", "Path", 1, true)));
            dialog.ShowContent(body);
            bool confirmed = false;
            dialog.AddClose("取消");
            dialog.AddButton("开始复制（" + files.Count + "）", () => { confirmed = true; dialog.Close(); }, true);
            dialog.ShowDialog();
            return confirmed;
        }

        private bool ShowCopyResult(CopyResult result, string output, string summary)
        {
            bool hasFailures = result.Failures.Count > 0;
            var dialog = new Dialog(this, "复制结果", 760, hasFailures ? 450 : 255, summary);
            var body = new DockPanel();
            if (output != null)
            {
                var path = new TextBox { Text = output, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 10) };
                AutomationProperties.SetName(path, "目标文件夹");
                DockPanel.SetDock(path, Dock.Top);
                body.Children.Add(path);
            }
            if (hasFailures)
                body.Children.Add(Dialog.Table(result.Failures.Select(failure => new { failure.File.Path, failure.Reason }).ToList(),
                    Dialog.Column("失败文件的来源路径", "Path", 1, true), Dialog.Column("失败原因", "Reason", 260)));
            else
                body.Children.Add(new Border());
            dialog.ShowContent(body);
            if (output != null) dialog.AddButton("打开目标文件夹", () => Launch("explorer.exe", "\"" + output + "\"", dialog));
            if (hasFailures) dialog.AddButton("仅重试失败项", () => { dialog.Tag = true; dialog.Close(); }, true);
            dialog.AddClose();
            dialog.ShowDialog();
            return dialog.Tag is bool;
        }

        private void OpenEntry(ArchiveEntry file)
        {
            if (file.IsFolder) Navigate(file.Path);
            else Launch(file.Path);
        }

        private void Launch(string executable, string arguments = "", Window owner = null)
        {
            try { Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true }); }
            catch (Exception error)
            {
                if (!ArchiveFiles.IsFileError(error) && !(error is Win32Exception)) throw;
                ShowError("无法打开", error, owner);
            }
        }

        private string PickFolder(Window owner, string title, string start)
        {
            try { return FolderPicker.Select(owner, title, start); }
            catch (System.Runtime.InteropServices.COMException error) { ShowError("无法选择文件夹", error, owner); return null; }
        }

        private void ShowError(string title, Exception error, Window owner = null)
        {
            MessageBox.Show(owner ?? this, ArchiveFiles.ErrorText(error), title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void SetProgress(string message, bool indeterminate)
        {
            SetStatus(message);
            progressBar.Value = 0;
            progressBar.IsIndeterminate = indeterminate;
            progressBar.Visibility = Visibility.Visible;
        }

        private void SetStatus(string message)
        {
            var label = Find<TextBlock>("StatusLabel");
            label.Text = message;
            label.ToolTip = message;
        }

        private sealed class FileRow : INotifyPropertyChanged
        {
            private readonly Workspace workspace;
            public ArchiveEntry Entry { get; private set; }
            public FileRow(Workspace workspace, ArchiveEntry entry) { this.workspace = workspace; Entry = entry; }
            public string Name { get { return Entry.Name; } }
            public string Department { get { return Entry.Department; } }
            public string Employee { get { return Entry.Employee; } }
            public string FormatLabel { get { return Entry.FormatLabel; } }
            public DateTime? Modified { get { return Entry.Modified; } }
            public string FullPath { get { return Entry.Path; } }
            public string DragHint { get { return IsFolder ? FullPath : FullPath + "\n拖到下方待复制区"; } }
            public string Location { get { return IsFolder ? "文件夹" : workspace.RelativeFolder(Entry.Parent); } }
            public bool IsFolder { get { return Entry.IsFolder; } }
            public Geometry Icon { get { return IsFolder ? FolderIcon : DocumentIcon; } }
            private bool IsPdf { get { return Entry.Format == "PDF"; } }
            private bool IsWord { get { return Entry.Format == "Word"; } }
            public Brush IconColor { get { return IsFolder ? FolderColor : IsPdf ? PdfColor : IsWord ? WordColor : FileColor; } }
            public Brush IconBackground { get { return IsFolder ? FolderTint : IsPdf ? PdfTint : IsWord ? WordTint : FileTint; } }
            private static readonly Brush FolderColor = Program.Brush("#815415");
            private static readonly Brush PdfColor = Program.Brush("#9C4241");
            private static readonly Brush WordColor = Program.Brush("#355F94");
            private static readonly Brush FileColor = Program.Brush("#3A7263");
            private static readonly Brush FolderTint = Program.Brush("#F6ECD8");
            private static readonly Brush PdfTint = Program.Brush("#FAEDEC");
            private static readonly Brush WordTint = Program.Brush("#E9F0FA");
            private static readonly Brush FileTint = Program.Brush("#E7F3EC");
            public bool Checked
            {
                get { return workspace.selection.ContainsKey(Entry.Path); }
                set
                {
                    if (IsFolder || Checked == value) return;
                    if (value) workspace.selection[Entry.Path] = Entry;
                    else workspace.RemoveFromSelection(Entry.Path);
                    workspace.UpdateSelectionStatus();
                    NotifyChecked();
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
            public void NotifyChecked() { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Checked")); }
        }

        private sealed class FilterOption : INotifyPropertyChanged
        {
            public string Value { get; private set; }
            public string Label { get; private set; }
            private bool isChecked;
            public FilterOption(string value, string label) { Value = value; Label = label; }
            public bool Checked
            {
                get { return isChecked; }
                set
                {
                    if (value == isChecked) return;
                    isChecked = value;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Checked"));
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
