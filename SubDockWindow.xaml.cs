using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Data;
using System.Globalization;
using System.Windows.Controls.Primitives;

namespace NexusDock
{
    public partial class SubDockWindow : Window, INotifyPropertyChanged
    {
        private AppShortcut _parentShortcut;
        private AppData _mainData;

        private Point _startPoint;
        private bool _isDragging = false;

        public static AppShortcut CurrentDragItem;
        public static AppShortcut CurrentDragSourceGroup;
        private const string DragFormat = "NexusSubDock_Item_Ref";

        public AppShortcut GroupData => _parentShortcut;

        public Brush SubDockBackgroundBrush
        {
            get
            {
                Color baseColor = Color.FromRgb(10, 10, 10);
                if (!string.IsNullOrEmpty(_parentShortcut.GroupBackgroundColor))
                {
                    try { baseColor = (Color)ColorConverter.ConvertFromString(_parentShortcut.GroupBackgroundColor); } catch { }
                }

                // Ignorujemy globalne ustawienie, aby suwak lokalny (GroupAlpha) miał pełną kontrolę
                double localTint = _parentShortcut.GroupAlpha;
                if (localTint <= 0.001) localTint = 1.0;

                double finalOpacity = Math.Clamp(localTint, 0.0, 1.0);
                byte alphaByte = (byte)(finalOpacity * 255);

                if (alphaByte < 5) alphaByte = 5;

                return new SolidColorBrush(Color.FromArgb(alphaByte, baseColor.R, baseColor.G, baseColor.B));
            }
        }

        public SubDockWindow(AppShortcut parentShortcut, AppData mainData)
        {
            InitializeComponent();
            _parentShortcut = parentShortcut;
            _mainData = mainData;

            this.Title = _parentShortcut.Name;
            SubItemsControl.ItemsSource = _parentShortcut.SubShortcuts;
            this.DataContext = _mainData.Config;

            _mainData.Config.PropertyChanged += Config_PropertyChanged;
            _parentShortcut.PropertyChanged += ParentShortcut_PropertyChanged;
        }

        private void Config_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DockConfig.EnableBlur))
            {
                OnPropertyChanged(nameof(SubDockBackgroundBrush));
                if (_mainData.Config.EnableBlur) EnableBlurEffect();
                else DisableBlurEffect();
            }
        }

        private void ParentShortcut_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppShortcut.GroupBackgroundColor) ||
                e.PropertyName == nameof(AppShortcut.GroupAlpha))
            {
                OnPropertyChanged(nameof(SubDockBackgroundBrush));
                if (e.PropertyName == nameof(AppShortcut.GroupAlpha)) SettingsManager.RequestSave(_mainData);
            }
        }

        private void ToggleRunContentOnStartup_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.RequestSave(_mainData);
        }

        private void ToggleTopmost_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = !this.Topmost;
            _parentShortcut.IsTopmost = this.Topmost;
            SettingsManager.RequestSave(_mainData);
        }

        // --- POPRAWIONA METODA URUCHAMIANIA (Fix dla cudzysłowów i argumentów) ---
        private void RunApp(AppShortcut shortcut, bool asAdmin)
        {
            if (string.IsNullOrWhiteSpace(shortcut.ExePath)) return;

            try
            {
                string cmd = shortcut.ExePath.Trim();
                string args = "";

                // 1. Najpierw używamy ShortcutHelper - on najlepiej radzi sobie z cudzysłowami "C:\..." --args
                var parsed = ShortcutHelper.ParseExeInfo(cmd);

                // Jeśli helper znalazł plik, używamy jego wyników
                if (!string.IsNullOrEmpty(parsed.Path) && (File.Exists(parsed.Path) || Directory.Exists(parsed.Path)))
                {
                    cmd = parsed.Path;
                    args = parsed.Args;
                }
                // 2. Jeśli nie, sprawdzamy prymitywne dzielenie po spacji (dla przypadków bez cudzysłowów, np. explorer.exe ms-screenclip:)
                else if (cmd.Contains(" ") && !cmd.StartsWith("\""))
                {
                    int spaceIndex = cmd.IndexOf(' ');
                    string potentialExe = cmd.Substring(0, spaceIndex);

                    if (File.Exists(potentialExe) || potentialExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        cmd = potentialExe;
                        args = cmd.Substring(spaceIndex + 1);
                    }
                }

                // 3. Fix dla protokołów explorera (np. ms-screenclip:)
                if ((cmd.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) || cmd.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                    && args.Contains(":"))
                {
                    cmd = args;
                    args = "";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = args,
                    UseShellExecute = true
                };

                if (asAdmin) psi.Verb = "runas";

                // Ustaw WorkingDirectory tylko jeśli to fizyczny plik
                if (File.Exists(cmd))
                    psi.WorkingDirectory = Path.GetDirectoryName(cmd);

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Nie udało się uruchomić: {ex.Message}");
            }
        }

        // --- RESZTA KODU OKNA ---

        private void SubDockWindow_LocationChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Normal && this.IsLoaded)
            {
                _parentShortcut.WindowLeft = this.Left;
                _parentShortcut.WindowTop = this.Top;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _mainData.Config.PropertyChanged -= Config_PropertyChanged;
            _parentShortcut.PropertyChanged -= ParentShortcut_PropertyChanged;
            this.LocationChanged -= SubDockWindow_LocationChanged;
            SettingsManager.RequestSave(_mainData);
            base.OnClosed(e);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_mainData.Config.EnableBlur) EnableBlurEffect();

            this.Topmost = _parentShortcut.IsTopmost;

            if (_parentShortcut.WindowWidth > 0 && _parentShortcut.WindowHeight > 0)
            {
                this.Width = _parentShortcut.WindowWidth;
                this.Height = _parentShortcut.WindowHeight;
            }
            else
            {
                if (_parentShortcut.ViewMode == SubDockViewMode.Grid)
                {
                    this.Width = _mainData.Config.SubDockWidth;
                    this.Height = _mainData.Config.SubDockHeight;
                }
                else
                {
                    this.Width = _mainData.Config.SubDockListWidth;
                    this.Height = _mainData.Config.SubDockListHeight;
                }
            }

            if (_parentShortcut.WindowLeft.HasValue && _parentShortcut.WindowTop.HasValue)
            {
                double left = _parentShortcut.WindowLeft.Value;
                double top = _parentShortcut.WindowTop.Value;
                if (IsValidPosition(left, top)) { this.Left = left; this.Top = top; }
                else CenterWindowNearMouse();
            }
            else CenterWindowNearMouse();

            AnimateEntrance();
            this.LocationChanged += SubDockWindow_LocationChanged;
        }

        private bool IsValidPosition(double left, double top)
        {
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vWidth = SystemParameters.VirtualScreenWidth;
            double vHeight = SystemParameters.VirtualScreenHeight;
            return left >= vLeft - 100 && left <= vLeft + vWidth - 50 && top >= vTop - 100 && top <= vTop + vHeight - 50;
        }

        private void CenterWindowNearMouse()
        {
            POINT p;
            if (GetCursorPos(out p)) { this.Left = p.X - (this.Width / 2); this.Top = p.Y - (this.Height / 2) - 50; }
            else { this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2; this.Top = (SystemParameters.PrimaryScreenHeight - this.Height) / 2; }
            if (this.Left < SystemParameters.VirtualScreenLeft) this.Left = SystemParameters.VirtualScreenLeft + 10;
            if (this.Top < SystemParameters.VirtualScreenTop) this.Top = SystemParameters.VirtualScreenTop + 10;
        }

        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width + e.HorizontalChange;
            double newHeight = this.Height + e.VerticalChange;
            if (newWidth < 150) newWidth = 150;
            if (newHeight < 150) newHeight = 150;
            this.Width = newWidth; this.Height = newHeight;
            _parentShortcut.WindowWidth = newWidth; _parentShortcut.WindowHeight = newHeight;
            SettingsManager.RequestSave(_mainData);
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer scv = (ScrollViewer)sender;
            scv.ScrollToHorizontalOffset(scv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private void Window_Deactivated(object sender, EventArgs e) { }

        private void AnimateEntrance()
        {
            DoubleAnimation opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            TranslateTransform trans = new TranslateTransform(0, 20);
            if (MainBorder != null) { MainBorder.RenderTransform = trans; DoubleAnimation slideAnim = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(250)); slideAnim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }; trans.BeginAnimation(TranslateTransform.YProperty, slideAnim); }
            this.BeginAnimation(Window.OpacityProperty, opacityAnim);
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) this.DragMove(); }
        private void CloseGroup_Click(object sender, RoutedEventArgs e) => this.Close();
        private void ToggleAutoOpen_Click(object sender, RoutedEventArgs e) { _parentShortcut.AutoOpen = !_parentShortcut.AutoOpen; SettingsManager.RequestSave(_mainData); }
        private void ChangeGroupColor_Click(object sender, RoutedEventArgs e) { if (sender is MenuItem item && item.Tag is string colorTag) { _parentShortcut.GroupBackgroundColor = colorTag == "Default" ? null : colorTag; SettingsManager.RequestSave(_mainData); } }
        private void ChangeGroupColor_Custom_Click(object sender, RoutedEventArgs e) { string current = _parentShortcut.GroupBackgroundColor ?? "#101010"; string newColor = SimpleInput.Show("Wpisz kod koloru HEX (np. #FF0000):", "Kolor tła grupy", current); if (!string.IsNullOrWhiteSpace(newColor)) { try { var color = ColorConverter.ConvertFromString(newColor); _parentShortcut.GroupBackgroundColor = newColor; SettingsManager.RequestSave(_mainData); } catch { MessageBox.Show("Nieprawidłowy format koloru.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning); } } }
        private void ChangeView_Click(object sender, RoutedEventArgs e) { if (sender is MenuItem item && item.Tag is string modeStr && Enum.TryParse(modeStr, out SubDockViewMode mode)) { _parentShortcut.ViewMode = mode; SettingsManager.RequestSave(_mainData); } }
        private void RunAsAdmin_Click(object sender, RoutedEventArgs e) { var menuItem = sender as MenuItem; var shortcut = menuItem.DataContext as AppShortcut; if (shortcut != null) RunApp(shortcut, asAdmin: true); }
        private void OpenFileLocation_Click(object sender, RoutedEventArgs e) { var menuItem = sender as MenuItem; var shortcut = menuItem.DataContext as AppShortcut; if (shortcut != null && !string.IsNullOrEmpty(shortcut.ExePath)) { try { string path = ShortcutHelper.ResolveShortcut(shortcut.ExePath); if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { var exeInfo = ShortcutHelper.ParseExeInfo(path); if (File.Exists(exeInfo.Path) || Directory.Exists(exeInfo.Path)) Process.Start("explorer.exe", $"/select, \"{exeInfo.Path}\""); } } catch (Exception ex) { MessageBox.Show("Nie można otworzyć lokalizacji: " + ex.Message); } } }
        private void EditShortcutPath_Click(object sender, RoutedEventArgs e) { var menuItem = sender as MenuItem; var shortcut = menuItem.DataContext as AppShortcut; if (shortcut != null) { string newPath = ShowInputDialog("Edytuj ścieżkę i argumenty:", "Edycja Skrótu", shortcut.ExePath); if (!string.IsNullOrWhiteSpace(newPath)) { shortcut.ExePath = newPath; SettingsManager.RequestSave(_mainData); } } }
        private void ShowNativeProperties_Click(object sender, RoutedEventArgs e) { var menuItem = sender as MenuItem; var shortcut = menuItem.DataContext as AppShortcut; if (shortcut != null && !string.IsNullOrEmpty(shortcut.ExePath)) { string path = ShortcutHelper.ResolveShortcut(shortcut.ExePath); if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { var exeInfo = ShortcutHelper.ParseExeInfo(path); if (!ShowFileProperties(exeInfo.Path)) MessageBox.Show("Nie udało się otworzyć okna właściwości."); } } }
        private void SubAppButton_Click(object sender, RoutedEventArgs e) { var button = sender as Button; var shortcut = button.DataContext as AppShortcut; if (shortcut != null) RunApp(shortcut, asAdmin: false); }

        private void SubAppButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { _startPoint = e.GetPosition(null); }
        private void SubAppButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging) return;
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point mousePos = e.GetPosition(null); Vector diff = _startPoint - mousePos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var button = sender as Button; var shortcut = button?.DataContext as AppShortcut;
                    if (shortcut != null)
                    {
                        _isDragging = true; CurrentDragItem = shortcut; CurrentDragSourceGroup = _parentShortcut;
                        DataObject dragData = new DataObject(); dragData.SetData(DragFormat, true);
                        try { string tempLnkPath = CreateTempShortcut(shortcut); if (!string.IsNullOrEmpty(tempLnkPath)) { var fileList = new System.Collections.Specialized.StringCollection(); fileList.Add(tempLnkPath); dragData.SetFileDropList(fileList); } } catch { }
                        try { DragDrop.DoDragDrop(button, dragData, DragDropEffects.Move | DragDropEffects.Copy); } finally { _isDragging = false; CurrentDragItem = null; CurrentDragSourceGroup = null; }
                    }
                }
            }
        }
        private string CreateTempShortcut(AppShortcut shortcut)
        {
            if (string.IsNullOrEmpty(shortcut.ExePath)) return null;
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "NexusDock_DragDrop"); if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                string linkName = $"{shortcut.Name}.lnk"; foreach (char c in Path.GetInvalidFileNameChars()) linkName = linkName.Replace(c, '_');
                string linkPath = Path.Combine(tempDir, linkName);
                var exeInfo = ShortcutHelper.ParseExeInfo(shortcut.ExePath); Type shellType = Type.GetTypeFromProgID("WScript.Shell"); dynamic shell = Activator.CreateInstance(shellType); dynamic link = shell.CreateShortcut(linkPath);
                link.TargetPath = exeInfo.Path; link.Arguments = exeInfo.Args; if (File.Exists(exeInfo.Path)) link.WorkingDirectory = Path.GetDirectoryName(exeInfo.Path);
                link.Save(); Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); return linkPath;
            }
            catch { return null; }
        }
        private void SubAppButton_DragOver(object sender, DragEventArgs e) { if (e.Data.GetDataPresent(DragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)) { e.Effects = DragDropEffects.Move; e.Handled = true; } }
        private void SubAppButton_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DragFormat) && CurrentDragItem != null && CurrentDragSourceGroup != null)
            {
                var targetItem = ((Button)sender).DataContext as AppShortcut; var targetGroup = _parentShortcut; var movedItem = CurrentDragItem; var sourceGroup = CurrentDragSourceGroup;
                if (sourceGroup == targetGroup) { int sourceIndex = sourceGroup.SubShortcuts.IndexOf(movedItem); int targetIndex = targetGroup.SubShortcuts.IndexOf(targetItem); if (sourceIndex != targetIndex && sourceIndex >= 0 && targetIndex >= 0) { sourceGroup.SubShortcuts.Move(sourceIndex, targetIndex); SettingsManager.RequestSave(_mainData); } }
                else { sourceGroup.SubShortcuts.Remove(movedItem); int targetIndex = targetGroup.SubShortcuts.IndexOf(targetItem); if (targetIndex >= 0) targetGroup.SubShortcuts.Insert(targetIndex, movedItem); else targetGroup.SubShortcuts.Add(movedItem); SettingsManager.RequestSave(_mainData); }
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)) { Window_Drop(sender, e); e.Handled = true; }
        }
        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DragFormat) && CurrentDragItem != null) { var targetGroup = _parentShortcut; var movedItem = CurrentDragItem; var sourceGroup = CurrentDragSourceGroup; if (sourceGroup != targetGroup) { sourceGroup.SubShortcuts.Remove(movedItem); targetGroup.SubShortcuts.Add(movedItem); SettingsManager.RequestSave(_mainData); } e.Handled = true; return; }
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                var newShortcuts = await Task.Run(() => { var list = new List<AppShortcut>(); foreach (string file in files) { if (file.Contains("NexusDock_DragDrop")) continue; try { string resolvedPath = ShortcutHelper.ResolveShortcut(file); string iconSource = File.Exists(resolvedPath) ? resolvedPath : file; string iconPath = IconHelper.ExtractIcon(iconSource); list.Add(new AppShortcut { Name = Path.GetFileNameWithoutExtension(file), ExePath = resolvedPath, IconPath = iconPath, IsSubDock = false }); } catch { } } return list; });
                foreach (var s in newShortcuts) _parentShortcut.SubShortcuts.Add(s); SettingsManager.RequestSave(_mainData); e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                try
                {
                    string text = (string)e.Data.GetData(DataFormats.Text); if (string.IsNullOrWhiteSpace(text)) return;
                    if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { Uri uri = new Uri(text); _parentShortcut.SubShortcuts.Add(new AppShortcut { Name = uri.Host, ExePath = text, IconPath = $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=128", IsSubDock = false }); SettingsManager.RequestSave(_mainData); e.Handled = true; }
                    else { if (File.Exists(text)) { string iconPath = IconHelper.ExtractIcon(text); _parentShortcut.SubShortcuts.Add(new AppShortcut { Name = Path.GetFileNameWithoutExtension(text), ExePath = text, IconPath = iconPath, IsSubDock = false }); SettingsManager.RequestSave(_mainData); e.Handled = true; } }
                }
                catch { }
            }
        }
        private void AddFolderShortcut_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "Wybierz folder" }; if (dialog.ShowDialog() == true) { string path = dialog.FolderName; string name = Path.GetFileName(path); if (string.IsNullOrEmpty(name)) name = path; string appDir = AppDomain.CurrentDomain.BaseDirectory; string icon = File.Exists(Path.Combine(appDir, "katalog.png")) ? Path.Combine(appDir, "katalog.png") : "https://cdn-icons-png.flaticon.com/512/716/716784.png"; _parentShortcut.SubShortcuts.Add(new AppShortcut { Name = name, ExePath = path, IconPath = icon, IsSubDock = false }); SettingsManager.RequestSave(_mainData); } }
        private void CreateDesktopShortcut_Click(object sender, RoutedEventArgs e) { var menuItem = sender as MenuItem; var shortcut = menuItem.DataContext as AppShortcut; if (shortcut != null && !string.IsNullOrEmpty(shortcut.ExePath)) { try { string tempLnk = CreateTempShortcut(shortcut); if (tempLnk != null) { string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop); string final = Path.Combine(desktop, Path.GetFileName(tempLnk)); int i = 1; while (File.Exists(final)) { final = Path.Combine(desktop, $"{shortcut.Name} ({i++}).lnk"); } File.Copy(tempLnk, final); } } catch (Exception ex) { MessageBox.Show("Błąd: " + ex.Message); } } }
        private void RenameSubShortcut_Click(object sender, RoutedEventArgs e) { var menuItem = sender as MenuItem; var shortcut = menuItem.DataContext as AppShortcut; if (shortcut != null) { string newName = ShowInputDialog("Nowa nazwa:", "Zmiana nazwy", shortcut.Name); if (!string.IsNullOrWhiteSpace(newName)) { shortcut.Name = newName; SettingsManager.RequestSave(_mainData); } } }
        private void ChangeSubIcon_Click(object sender, RoutedEventArgs e) { var menuItem = sender as MenuItem; var shortcut = menuItem.DataContext as AppShortcut; if (shortcut != null) { OpenFileDialog dlg = new OpenFileDialog { Filter = "Obrazy|*.png;*.jpg;*.ico|Wszystkie|*.*" }; if (dlg.ShowDialog() == true) { shortcut.IconPath = dlg.FileName; SettingsManager.RequestSave(_mainData); } } }
        private void DeleteSubShortcut_Click(object sender, RoutedEventArgs e) { var menuItem = sender as MenuItem; var shortcut = menuItem.DataContext as AppShortcut; if (shortcut != null) { if (MessageBox.Show($"Usunąć '{shortcut.Name}'?", "Usuwanie", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { _parentShortcut.SubShortcuts.Remove(shortcut); SettingsManager.RequestSave(_mainData); } } }
        private string ShowInputDialog(string prompt, string title, string defaultText) { Window window = new Window { Title = title, Width = 400, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) }; StackPanel stack = new StackPanel { Margin = new Thickness(10) }; TextBlock textBlock = new TextBlock { Text = prompt, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 5) }; TextBox textBox = new TextBox { Text = defaultText, Margin = new Thickness(0, 0, 0, 10), Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)), Foreground = Brushes.White, BorderBrush = Brushes.Gray }; Button btnOk = new Button { Content = "OK", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(15, 2, 15, 2) }; string result = null; btnOk.Click += (s, e) => { result = textBox.Text; window.DialogResult = true; window.Close(); }; stack.Children.Add(textBlock); stack.Children.Add(textBox); stack.Children.Add(btnOk); window.Content = stack; window.Loaded += (s, e) => { textBox.Focus(); textBox.SelectAll(); }; if (window.ShowDialog() == true) return result; return null; }
        public event PropertyChangedEventHandler PropertyChanged; protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT lpPoint); [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        [DllImport("shell32.dll", CharSet = CharSet.Auto)] static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] public struct SHELLEXECUTEINFO { public int cbSize; public uint fMask; public IntPtr hwnd; [MarshalAs(UnmanagedType.LPTStr)] public string lpVerb; [MarshalAs(UnmanagedType.LPTStr)] public string lpFile; [MarshalAs(UnmanagedType.LPTStr)] public string lpParameters; [MarshalAs(UnmanagedType.LPTStr)] public string lpDirectory; public int nShow; public IntPtr hInstApp; public IntPtr lpIDList; [MarshalAs(UnmanagedType.LPTStr)] public string lpClass; public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon; public IntPtr hProcess; }
        private const int SW_SHOW = 5; private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        private bool ShowFileProperties(string Filename) { SHELLEXECUTEINFO info = new SHELLEXECUTEINFO(); info.cbSize = Marshal.SizeOf(info); info.lpVerb = "properties"; info.lpFile = Filename; info.nShow = SW_SHOW; info.fMask = SEE_MASK_INVOKEIDLIST; return ShellExecuteEx(ref info); }
        private void EnableBlurEffect() { var windowHelper = new WindowInteropHelper(this); var accent = new AccentPolicy(); accent.AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND; accent.GradientColor = (0 << 24) | (10 << 16) | (10 << 8) | 10; var accentStructSize = Marshal.SizeOf(accent); var accentPtr = Marshal.AllocHGlobal(accentStructSize); Marshal.StructureToPtr(accent, accentPtr, false); var data = new WindowCompositionAttributeData(); data.Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY; data.SizeOfData = accentStructSize; data.Data = accentPtr; SetWindowCompositionAttribute(windowHelper.Handle, ref data); Marshal.FreeHGlobal(accentPtr); }
        private void DisableBlurEffect()
        {
            var windowHelper = new WindowInteropHelper(this);
            var accent = new AccentPolicy { AccentState = AccentState.ACCENT_DISABLED };
            var accentStructSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentStructSize);
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = accentStructSize,
                Data = accentPtr
            };
            int result = SetWindowCompositionAttribute(windowHelper.Handle, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }
        [DllImport("user32.dll")] internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
        [StructLayout(LayoutKind.Sequential)] internal struct WindowCompositionAttributeData { public WindowCompositionAttribute Attribute; public IntPtr Data; public int SizeOfData; }
        internal enum WindowCompositionAttribute { WCA_ACCENT_POLICY = 19 }
        internal enum AccentState { ACCENT_DISABLED = 0, ACCENT_ENABLE_GRADIENT = 1, ACCENT_ENABLE_TRANSPARENTGRADIENT = 2, ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4, ACCENT_INVALID_STATE = 5 }
        [StructLayout(LayoutKind.Sequential)] internal struct AccentPolicy { public AccentState AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    }
}