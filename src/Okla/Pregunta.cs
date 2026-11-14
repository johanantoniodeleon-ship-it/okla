using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Okla
{
    /// <summary>
    /// Pregunta pequeña en la esquina (encima de la barra de tareas) con dos botones. No roba el foco y,
    /// si nadie responde, se cierra sola contestando "no". Se usa al desconectar el cargador.
    /// </summary>
    internal sealed class Pregunta : Window
    {
        private Action<bool>? _respuesta;
        private readonly DispatcherTimer _limite;

        public Pregunta(string titulo, string texto, string colorHex, string? iconoModo, string si, string no, int segundos, Action<bool> respuesta)
        {
            _respuesta = respuesta;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;

            var acento = (Color)ColorConverter.ConvertFromString(colorHex);
            var fila = new DockPanel();
            if (iconoModo != null && IconosModos.Wpf(iconoModo) is { } src)
            {
                var img = new Image { Source = src, Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                DockPanel.SetDock(img, Dock.Left); fila.Children.Add(img);
            }
            var sp = new StackPanel { Width = 280 };
            sp.Children.Add(new TextBlock { Text = titulo, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            sp.Children.Add(new TextBlock { Text = texto, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
                                            Foreground = new SolidColorBrush(Color.FromRgb(0xb8, 0xbc, 0xc6)) });
            var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            botones.Children.Add(Boton(no, false, Color.FromRgb(0x3a, 0x3e, 0x48), Colors.White));
            botones.Children.Add(Boton(si, true, acento, Color.FromRgb(0x14, 0x16, 0x1c)));
            sp.Children.Add(botones);
            fila.Children.Add(sp);

            Content = new Border
            {
                Child = fila, Padding = new Thickness(18, 16, 18, 16), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0xf2, 0x1b, 0x1e, 0x25)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, acento.R, acento.G, acento.B)), BorderThickness = new Thickness(1),
            };

            Loaded += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth - 16;
                Top = area.Bottom - ActualHeight - 16;
            };
            _limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(segundos) };
            _limite.Tick += (_, _) => Responder(false);
            _limite.Start();
        }

        private Button Boton(string texto, bool valor, Color fondo, Color letra)
        {
            var b = new Button
            {
                Content = texto, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 6, 16, 6), FontSize = 13,
                Background = new SolidColorBrush(fondo), Foreground = new SolidColorBrush(letra), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => Responder(valor);
            return b;
        }

        /// <summary>Cierra la pregunta sin contestar (p. ej. porque el cargador volvió).</summary>
        public void Cancelar() { _respuesta = null; Responder(false); }

        private void Responder(bool valor)
        {
            _limite.Stop();
            var r = _respuesta; _respuesta = null;
            if (IsVisible) Close();
            r?.Invoke(valor);
        }
    }
}
