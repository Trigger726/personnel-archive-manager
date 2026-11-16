using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveManager
{
    internal sealed class Dialog : Window
    {
        private readonly DockPanel body = new DockPanel { Margin = new Thickness(22) };
        private readonly StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };

        public Dialog(Window owner, string title, double width, double height, string description)
        {
            Owner = owner;
            Title = title;
            Width = width;
            Height = height;
            MinWidth = width;
            MinHeight = height;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = owner.FontFamily;
            FontSize = owner.FontSize;
            Foreground = owner.Foreground;
            Background = Brushes.White;
            UseLayoutRounding = true;
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            heading.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.Medium });
            if (!string.IsNullOrEmpty(description))
            {
                var subtitle = new TextBlock { Text = description, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
                subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                heading.Children.Add(subtitle);
            }
            DockPanel.SetDock(heading, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            body.Children.Add(heading);
            body.Children.Add(buttons);
            Content = body;
            if (InputManager.Current.MostRecentInputDevice is MouseDevice)
                ContentRendered += (sender, args) => Appearance.RevealDialog(body);
        }

        public void ShowContent(UIElement content) { body.Children.Add(content); }

        public Button AddButton(string text, Action action, bool primary = false)
        {
            var button = Program.Button(text, action, primary);
            buttons.Children.Add(button);
            return button;
        }

        public void AddClose(string text = "关闭")
        {
            AddButton(text, Close).IsCancel = true;
        }

        public static DataGrid Table(object rows, params DataGridColumn[] columns)
        {
            var table = new DataGrid
            {
                EnableRowVirtualization = true, EnableColumnVirtualization = true,
                AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                IsReadOnly = true, ItemsSource = (System.Collections.IEnumerable)rows, SelectionMode = DataGridSelectionMode.Single,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, RowHeight = 40, ColumnHeaderHeight = 36,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Background = Brushes.White
            };
            table.SetResourceReference(DataGrid.BorderBrushProperty, "Rule");
            table.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Rule");
            table.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "TableHeader");
            table.SetResourceReference(DataGrid.RowStyleProperty, "ArchiveTableRow");
            table.SetResourceReference(DataGrid.CellStyleProperty, "ArchiveTableCell");
            table.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Wash");
            foreach (var column in columns) table.Columns.Add(column);
            return table;
        }

        public static DataGridTextColumn Column(string header, string property, double width, bool star = false)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Text")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self) }));
            return new DataGridTextColumn
            {
                Header = header, Binding = new System.Windows.Data.Binding(property), ElementStyle = style,
                Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel)
            };
        }
    }

    internal static class FolderPicker
    {
        public static string Select(Window owner, string title, string start = null)
        {
            var dialog = (IFileDialog)new FileOpenDialog();
            IShellItem initial = null;
            IShellItem result = null;
            try
            {
                dialog.SetOptions(0x20 | 0x40 | 0x800 | 0x02000000); // Pick folders, filesystem paths, existing paths, no recent items.
                dialog.SetTitle(title);
                dialog.SetOkButtonLabel("选择文件夹");
                var shellItemId = typeof(IShellItem).GUID;
                if (!string.IsNullOrEmpty(start) && SHCreateItemFromParsingName(start, IntPtr.Zero, ref shellItemId, out initial) >= 0)
                    dialog.SetFolder(initial);
                int status = dialog.Show(new WindowInteropHelper(owner).Handle);
                if (status == unchecked((int)0x800704C7)) return null;
                Marshal.ThrowExceptionForHR(status);
                dialog.GetResult(out result);
                IntPtr name;
                result.GetDisplayName(0x80058000, out name);
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally
            {
                if (initial != null) Marshal.ReleaseComObject(initial);
                if (result != null) Marshal.ReleaseComObject(result);
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem result);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint type, out IntPtr name);
        }
    }
}
