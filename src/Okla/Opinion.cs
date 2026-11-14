using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Okla
{
    /// <summary>
    /// Ventana pequeña para mandar una opinión (punto 15). Llega a Supabase (tabla opiniones) con la versión
    /// y el modo en uso; el dueño de okla la lee desde el panel. Solo se manda si el usuario pulsa Enviar.
    /// </summary>
    internal sealed class OpinionWindow : Window
    {
        private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xe6, 0xe6, 0xea));
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8a, 0x8f, 0x9a));
        private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x7f, 0xd8, 0xbe));
        private static readonly Brush Mal = new SolidColorBrush(Color.FromRgb(0xe0, 0x6c, 0x75));
        private const int Max = 2000;

        public OpinionWindow(string? modo, string? nombreModo)
        {
            Title = "okla · opinión";
            Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1c));
            try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "okla.ico"))); } catch { }

            var sp = new StackPanel { Margin = new Thickness(24) };
            sp.Children.Add(new TextBlock { Text = "¿Qué le cambiarías a okla?", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Fg });
            sp.Children.Add(new TextBlock
            {
                Text = "Un modo que sobra o falta, un fallo, un fondo que no te gusta, una idea loca… Todo sirve para decidir lo próximo.",
                TextWrapping = TextWrapping.Wrap, Foreground = Dim, Margin = new Thickness(0, 4, 0, 14),
            });

            var caja = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = Max, Height = 150, FontSize = 13.5, Padding = new Thickness(8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x20, 0x28)),
                Foreground = Fg, CaretBrush = Fg, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2f, 0x33, 0x3d)),
            };
            sp.Children.Add(caja);

            var info = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var cuenta = new TextBlock { Foreground = Dim, FontSize = 11, Text = $"0 / {Max}", Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(cuenta, Dock.Right); info.Children.Add(cuenta);
            info.Children.Add(new TextBlock
            {
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Text = "Se envía el texto, la versión de okla" + (nombreModo != null ? $" y el modo en uso ({nombreModo})" : "") + ". No pongas datos personales.",
            });
            sp.Children.Add(info);

            var estado = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            sp.Children.Add(estado);

            var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancelar = new Button { Content = "Cancelar", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            var enviar = new Button { Content = "Enviar", Padding = new Thickness(22, 7, 22, 7), IsEnabled = false };
            InstaladorWindow.Estilo(enviar); InstaladorWindow.Estilo(cancelar, true);
            cancelar.Click += (_, _) => Close();
            botones.Children.Add(cancelar); botones.Children.Add(enviar);
            sp.Children.Add(botones);
            Content = sp;

            caja.TextChanged += (_, _) =>
            {
                cuenta.Text = $"{caja.Text.Length} / {Max}";
                enviar.IsEnabled = caja.Text.Trim().Length > 0;
            };
            enviar.Click += async (_, _) =>
            {
                enviar.IsEnabled = false; caja.IsReadOnly = true; enviar.Content = "Enviando…";
                bool ok = await Red.EnviarOpinion(caja.Text, modo);
                estado.Visibility = Visibility.Visible;
                if (ok)
                {
                    Log.Info("Opinión enviada");
                    estado.Foreground = Ok; estado.Text = "¡Llegó! Gracias. Misión cumplida, +100 XP.";
                    enviar.Content = "Enviado"; cancelar.Content = "Cerrar";
                }
                else
                {
                    estado.Foreground = Mal; estado.Text = "No se pudo enviar (¿sin internet?). Tu texto sigue aquí; prueba otra vez.";
                    enviar.Content = "Reintentar"; enviar.IsEnabled = true; caja.IsReadOnly = false;
                }
            };
            Loaded += (_, _) => { caja.Focus(); Keyboard.Focus(caja); };
        }
    }
}
