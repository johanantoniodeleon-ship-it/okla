using System;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Okla
{
    /// <summary>Cartelito breve arriba al centro. No roba el foco y los clics lo atraviesan.</summary>
    internal static class Toast
    {
        private static Window? _current;

        public static void Show(string titulo, string subtitulo, string colorHex, Transition t, string? soundKey)
        {
            _current?.Close();

            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                                   Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 12, 0),
                                   VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = titulo, FontSize = 17, FontWeight = FontWeights.SemiBold,
                                              Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4)),
                                              FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
            if (!string.IsNullOrWhiteSpace(subtitulo))
                text.Children.Add(new TextBlock { Text = subtitulo, FontSize = 12,
                                                  Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xB1, 0xC1)),
                                                  FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(text);

            var card = new Border
            {
                Child = row,
                Padding = new Thickness(20, 12, 24, 12),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x20, 0x28)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(16)
            };

            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight, Content = card, Opacity = 0
            };
            w.SourceInitialized += (_, _) => MakeClickThrough(w);
            w.Loaded += (_, _) =>
            {
                var wa = SystemParameters.WorkArea;
                w.Left = wa.Left + (wa.Width - w.ActualWidth) / 2;
                w.Top = wa.Top + 28;
            };
            _current = w;
            w.Show();

            bool instant = t == Transition.Instantanea;
            double fadeIn = instant ? 0 : t == Transition.FundidoLento ? 350 : 160;
            double hold = instant ? 800 : 1400;
            double fadeOut = instant ? 0 : t == Transition.FundidoLento ? 500 : 280;

            if (instant) w.Opacity = 1;
            else w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeIn)));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(fadeIn + hold) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (instant) { w.Close(); return; }
                var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(fadeOut));
                a.Completed += (_, _) => w.Close();
                w.BeginAnimation(UIElement.OpacityProperty, a);
            };
            timer.Start();

            PlaySound(soundKey);
        }

private static readonly MediaPlayer Player = new();

        /// <summary>Busca recursos\sonidos\modo-X.(wav|mp3) y si no, cambio.(wav|mp3). Si no hay ninguno, silencio.</summary>
        internal static void PlaySound(string? key)
        {
            try
            {
                string? Find(string name)
                {
                    foreach (var ext in new[] { ".wav", ".mp3" })
                    {
                        var f = Path.Combine(Paths.Sonidos, name + ext);
                        if (File.Exists(f)) return f;
                    }
                    return null;
                }
                var file = (key != null ? Find($"modo-{key}") : null) ?? Find("cambio");
                if (file == null) return;
                Player.Open(new Uri(file));
                Player.Volume = 0.6;
                Player.Play();
            }
            catch (Exception ex) { Log.Error("Sonido: " + ex.Message); }
        }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        internal static void MakeClickThrough(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }
    }
}
