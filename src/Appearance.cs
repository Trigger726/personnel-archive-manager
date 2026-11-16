using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ArchiveManager
{
    internal static class Appearance
    {
        public static bool MotionEnabled { get { return SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast; } }

        public static FontFamily InterfaceFont(string appDirectory)
        {
            string directory = Path.Combine(appDirectory, "Fonts");
            const string family = "Source Han Sans SC";
            const string fallback = "Microsoft YaHei UI, Segoe UI";
            if (Directory.Exists(directory))
            {
                var location = new Uri(directory + Path.DirectorySeparatorChar);
                if (Fonts.GetFontFamilies(location).Any(font => font.FamilyNames.Values.Contains(family)))
                    return new FontFamily(location, "./#" + family + ", " + fallback);
            }
            return new FontFamily(fallback);
        }

        public static void RevealDialog(FrameworkElement content)
        {
            if (!MotionEnabled) return;
            var scale = new ScaleTransform();
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransform = scale;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var expand = new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(190), FillBehavior.Stop)
                { EasingFunction = easing };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
            content.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(150), FillBehavior.Stop) { EasingFunction = easing });
        }

        public static void Reveal(FrameworkElement element, double initialOpacity, int delay = 0)
        {
            var fade = new DoubleAnimation(initialOpacity, 1, TimeSpan.FromMilliseconds(150), FillBehavior.Stop)
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public static void StaggerOnHover(Panel panel)
        {
            panel.MouseEnter += (sender, args) =>
            {
                if (!MotionEnabled) return;
                int delay = 0;
                foreach (var button in panel.Children.OfType<Button>().Where(button => button.IsEnabled))
                {
                    Reveal(button, 0.72, delay);
                    delay += 30;
                }
            };
            panel.MouseLeave += (sender, args) =>
            {
                foreach (var button in panel.Children.OfType<Button>())
                    button.BeginAnimation(UIElement.OpacityProperty, null);
            };
        }

        public static void RevealDirectoryChildren(IEnumerable<FrameworkElement> headers)
        {
            if (!MotionEnabled) return;
            int[] delays = { 0, 38, 67, 88, 100 };
            int index = 0;
            foreach (var header in headers)
            {
                int delay = delays[Math.Min(index, delays.Length - 1)];
                double offset = index == 0 ? 8 : index < 3 ? 5 : 3;
                RevealDirectoryHeader(header, delay, offset);
                index++;
            }
        }

        private static void RevealDirectoryHeader(FrameworkElement header, int delay, double offset)
        {
            var shift = new TranslateTransform();
            header.RenderTransform = shift;
            var fade = DirectoryEntrance(0.28, 1, delay);
            EventHandler finished = null;
            DependencyPropertyChangedEventHandler visibilityChanged = null;
            finished = (sender, args) =>
            {
                header.IsVisibleChanged -= visibilityChanged;
                if (!ReferenceEquals(header.RenderTransform, shift)) return;
                header.BeginAnimation(UIElement.OpacityProperty, null);
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                header.ClearValue(UIElement.RenderTransformProperty);
            };
            visibilityChanged = (sender, args) =>
            {
                if (!(bool)args.NewValue) finished(sender, EventArgs.Empty);
            };
            fade.Completed += finished;
            header.IsVisibleChanged += visibilityChanged;
            shift.BeginAnimation(TranslateTransform.YProperty, DirectoryEntrance(offset, 0, delay));
            header.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static DoubleAnimationUsingKeyFrames DirectoryEntrance(double from, double to, int delay)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(delay + 200),
                FillBehavior = FillBehavior.Stop
            };
            // Hold the initial value during the stagger delay so the row cannot flash at full opacity.
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, TimeSpan.Zero));
            if (delay > 0)
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, TimeSpan.FromMilliseconds(delay)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, TimeSpan.FromMilliseconds(delay + 200),
                new KeySpline(0.16, 1, 0.3, 1)));
            return animation;
        }

        public static void GlitchTitle(TextBlock title, TextBlock cyan, TextBlock rose)
        {
            if (!MotionEnabled || title.ActualWidth <= 0 || title.ActualHeight <= 0) return;
            GlitchLayer(cyan, title, 0.27, 1.8, 0);
            GlitchLayer(rose, title, 0.57, -1.8, 35);
        }

        private static void GlitchLayer(TextBlock layer, TextBlock title, double slice, double offset, int delay)
        {
            layer.Visibility = Visibility.Visible;
            layer.Opacity = 0;
            layer.Clip = new RectangleGeometry(new Rect(0, title.ActualHeight * slice, title.ActualWidth, title.ActualHeight * 0.24));
            var shift = new TranslateTransform();
            layer.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(220),
                FillBehavior = FillBehavior.Stop,
                BeginTime = TimeSpan.FromMilliseconds(delay),
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(offset, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(-offset * 0.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))),
                    new DiscreteDoubleKeyFrame(offset * 0.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)))
                }
            });
            var fade = new DoubleAnimation(0.82, 0, TimeSpan.FromMilliseconds(220), FillBehavior.Stop)
                { BeginTime = TimeSpan.FromMilliseconds(delay) };
            fade.Completed += (sender, args) =>
            {
                if (ReferenceEquals(layer.RenderTransform, shift)) layer.Visibility = Visibility.Hidden;
            };
            layer.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
