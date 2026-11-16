using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

[assembly: AssemblyTitle("人事档案文件管理")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace ArchiveManager
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml"))
                app.Resources = (ResourceDictionary)XamlReader.Load(stream);
            app.Run(new Workspace());
        }

        public static SolidColorBrush Brush(string color)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(color);
        }

        public static Button Button(string text, Action action, bool primary = false)
        {
            var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0) };
            if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            button.Click += (sender, args) => action();
            return button;
        }
    }
}
