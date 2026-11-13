using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Okla
{
    /// <summary>Acceso a la vista del escritorio de Explorer (la misma que usa "Ver > Iconos grandes").</summary>
    internal static class DesktopShell
    {
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IShellWindows
        {
            [PreserveSig] int get_Count(out int c);
            [PreserveSig] int Item(object index, [MarshalAs(UnmanagedType.IDispatch)] out object d);
            [PreserveSig] int _NewEnum(out IntPtr e);
            [PreserveSig] int Register(IntPtr a, int b, int c, out int d);
            [PreserveSig] int RegisterPending(int a, IntPtr b, IntPtr c, int d, out int e);
            [PreserveSig] int Revoke(int a);
            [PreserveSig] int OnNavigate(int a, IntPtr b);
            [PreserveSig] int OnActivated(int a, short b);
            [PreserveSig] int FindWindowSW(ref object loc, ref object root, int swClass, out int hwnd, int options,
                                           [MarshalAs(UnmanagedType.IDispatch)] out object disp);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid sid, ref Guid iid, out IntPtr obj);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void GetWindow(); void ContextSensitiveHelp();
            void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB(); void SetStatusTextSB(); void EnableModelessSB();
            void TranslateAcceleratorSB(); void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
            [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
        }

        [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFolderView2
        {
            // IFolderView
            void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder(); void Item();
            [PreserveSig] int ItemCount(uint flags, out int count);
            void Items(); void GetSelectionMarkedItem(); void GetFocusedItem(); void GetItemPosition(); void GetSpacing();
            void GetDefaultSpacing(); void GetAutoArrange(); void SelectItem(); void SelectAndPositionItems();
            // IFolderView2 (hasta SetViewModeAndIconSize)
            void SetGroupBy(); void GetGroupBy(); void SetViewProperty(); void GetViewProperty(); void SetTileViewProperties();
            void SetExtendedTileViewProperties(); void SetText(); void SetCurrentFolderFlags(); void GetCurrentFolderFlags();
            void GetSortColumnCount(); void SetSortColumns(); void GetSortColumns(); void GetItem(); void GetVisibleItem();
            void GetSelectedItem(); void GetSelection(); void GetSelectionState(); void InvokeVerbOnSelection();
            [PreserveSig] int SetViewModeAndIconSize(int mode, int size);
            [PreserveSig] int GetViewModeAndIconSize(out int mode, out int size);
        }

        /// <summary>La vista de iconos del escritorio, o null si Explorer no está listo.</summary>
        public static IFolderView2? View()
        {
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!;
                var sw = (IShellWindows)Activator.CreateInstance(t)!;
                object loc = 0 /*CSIDL_DESKTOP*/, root = null!;
                if (sw.FindWindowSW(ref loc, ref root, 8 /*SWC_DESKTOP*/, out _, 1 /*SWFO_NEEDDISPATCH*/, out var disp) != 0 || disp == null) return null;
                var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
                var iid = typeof(IShellBrowser).GUID;
                if (((IServiceProvider)disp).QueryService(ref sid, ref iid, out var pb) != 0) return null;
                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pb);
                Marshal.Release(pb);
                if (browser.QueryActiveShellView(out var view) != 0) return null;
                return view as IFolderView2;
            }
            catch (Exception ex) { Log.Error("Vista del escritorio: " + ex.Message); return null; }
        }

        public static int ItemCount() => View() is { } v && v.ItemCount(2 /*SVGIO_ALLVIEW*/, out var n) == 0 ? n : -1;

        /// <summary>Pide a Explorer que vuelva a leer el escritorio (F5).</summary>
        public static void Refresh()
        {
            IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr w = IntPtr.Zero;
            while (defView == IntPtr.Zero && (w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
                defView = Native.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero) Native.SendMessage(defView, 0x0111, (IntPtr)0x7103 /*Actualizar*/, IntPtr.Zero);
        }
    }

    // ---------------------------------------------------------------- tamaño de los iconos del escritorio
    internal sealed class IconSizeSetting : Setting
    {
        public override string Id => "tamano-iconos";
        public override string Nombre => "Tamaño de iconos del escritorio";
        public override string Read()
        {
            var v = DesktopShell.View();
            return v != null && v.GetViewModeAndIconSize(out _, out var size) == 0 ? size.ToString() : "";
        }
        public override string Normalize(string v) => v.Trim().ToLowerInvariant() switch
        {
            "pequeños" or "pequenos" => "32",
            "medianos" => "48",
            "grandes" => "96",
            var x => x
        };
        public override void Apply(string v)
        {
            if (!int.TryParse(Normalize(v), out var size) || size < 16 || size > 256) throw new ArgumentException("tamaño de iconos: " + v);
            var view = DesktopShell.View() ?? throw new InvalidOperationException("el escritorio no está listo");
            Marshal.ThrowExceptionForHR(view.SetViewModeAndIconSize(1 /*FVM_ICON*/, size));
        }
    }

    // ---------------------------------------------------------------- carpeta del escritorio (accesos distintos por modo)
    /// <summary>
    /// Cambia la carpeta que Windows usa como Escritorio. No mueve archivos: cada modo tiene su carpeta en
    /// okla\escritorios y el pánico vuelve a la original. Fuera de la original se ocultan los accesos del
    /// escritorio público (Steam, VLC...) para que cada modo muestre solo los suyos.
    /// </summary>
    internal sealed class DesktopFolderSetting : Setting
    {
        private static readonly Guid FolderDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHSetKnownFolderPath(ref Guid id, uint flags, IntPtr token, string path);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(int ev, uint flags, string? a, string? b);

        private static string Public => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        private static string HiddenList => Path.Combine(Paths.Config, "escritorio-publico-oculto.txt");

        public override string Id => "escritorio";
        public override string Nombre => "Escritorio del modo";

        public override string Read()
        {
            var g = FolderDesktop;
            if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out var p) != 0) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; } finally { Marshal.FreeCoTaskMem(p); }
        }

        public override string Normalize(string v) => v.Trim().TrimEnd('\\');

        public override bool Matches(string target, string current) =>
            string.Equals(Normalize(target), Normalize(current), StringComparison.OrdinalIgnoreCase);

        public override void Apply(string v)
        {
            var path = Normalize(v);
            Directory.CreateDirectory(path);
            var old = Read();
            var g = FolderDesktop;
            Marshal.ThrowExceptionForHR(SHSetKnownFolderPath(ref g, 0, IntPtr.Zero, path));

            bool original = !path.StartsWith(Paths.Root, StringComparison.OrdinalIgnoreCase);
            if (original) ShowPublic(); else HidePublic();

            SHChangeNotify(0x00001000 /*SHCNE_UPDATEDIR*/, 0x0005 /*SHCNF_PATHW*/, old, null);
            SHChangeNotify(0x00001000, 0x0005, path, null);
            SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, null, null);
            DesktopShell.Refresh();
        }

        /// <summary>Oculta los accesos del escritorio público y apunta cuáles, para devolverlos luego.</summary>
        private static void HidePublic()
        {
            try
            {
                var ya = File.Exists(HiddenList) ? File.ReadAllLines(HiddenList).ToHashSet(StringComparer.OrdinalIgnoreCase) : new();
                foreach (var f in Directory.GetFiles(Public).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                {
                    var a = File.GetAttributes(f);
                    if ((a & FileAttributes.Hidden) != 0) continue; // ya oculto (por okla o por el usuario)
                    File.SetAttributes(f, a | FileAttributes.Hidden);
                    ya.Add(f);
                }
                File.WriteAllLines(HiddenList, ya);
            }
            catch (Exception ex) { Log.Error("No pude ocultar el escritorio público: " + ex.Message); }
        }

        private static void ShowPublic()
        {
            try
            {
                if (!File.Exists(HiddenList)) return;
                foreach (var f in File.ReadAllLines(HiddenList).Where(File.Exists))
                    File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.Hidden);
                File.Delete(HiddenList);
            }
            catch (Exception ex) { Log.Error("No pude mostrar el escritorio público: " + ex.Message); }
        }
    }
}
