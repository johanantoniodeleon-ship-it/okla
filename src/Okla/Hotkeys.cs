using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Okla
{
    /// <summary>Atajos globales (funcionan aunque okla no tenga el foco) con RegisterHotKey.</summary>
    internal sealed class Hotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly HwndSource _src;
        private readonly Dictionary<int, Action> _actions = new();
        private int _nextId = 1;

        public Hotkeys()
        {
            // Ventana invisible solo para recibir mensajes.
            var p = new HwndSourceParameters("okla-hotkeys") { Width = 0, Height = 0, WindowStyle = 0 };
            _src = new HwndSource(p);
            _src.AddHook(WndProc);
        }

        /// <returns>false si otra app ya usa esa combinación.</returns>
        public bool Register(uint mods, uint vk, Action action)
        {
            int id = _nextId++;
            if (!RegisterHotKey(_src.Handle, id, mods | MOD_NOREPEAT, vk)) return false;
            _actions[id] = action;
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
            {
                handled = true;
                try { a(); } catch (Exception ex) { Log.Error("Atajo: " + ex); }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in _actions.Keys) UnregisterHotKey(_src.Handle, id);
            _src.Dispose();
        }
    }
}
