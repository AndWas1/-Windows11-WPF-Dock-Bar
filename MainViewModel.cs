using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NexusDock
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private DispatcherTimer _processCheckTimer;

        public AppData Data { get; private set; }

        public ICommand ChangeThemeCommand { get; private set; }
        public ICommand ChangeAccentCommand { get; private set; }

        public MainViewModel()
        {
            Data = SettingsManager.GetSharedData();
            InitializeDefaultShortcuts();

            // NOWOŚĆ: Przywracanie zapisanego motywu i akcentu przy starcie
            ApplySavedTheme();

            // Inicjalizacja komend z logiką zapisu
            ChangeThemeCommand = new RelayCommand(param =>
            {
                if (param is ThemeType theme)
                {
                    ChangeTheme(theme);
                }
                else if (param is string themeName && Enum.TryParse(themeName, out ThemeType parsedTheme))
                {
                    ChangeTheme(parsedTheme);
                }
            });

            ChangeAccentCommand = new RelayCommand(param =>
            {
                if (param is string hexColor)
                {
                    ThemeManager.SetAccentColor(hexColor);
                    // Zapisujemy niestandardowy akcent
                    Data.Config.CustomAccentColor = hexColor;
                    RequestSave();
                }
            });

            Data.Config.PropertyChanged += Config_PropertyChanged;
            Data.Shortcuts.CollectionChanged += (s, e) => RequestSave();

            _processCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _processCheckTimer.Tick += ProcessCheckTimer_Tick;
            _processCheckTimer.Start();
        }

        private void InitializeDefaultShortcuts()
        {
            if (!Data.Shortcuts.Any(s => s.IsSettings))
            {
                Data.Shortcuts.Add(new AppShortcut { Name = "Ustawienia", IconPath = "https://cdn-icons-png.flaticon.com/512/3524/3524659.png", IsSettings = true });
                RequestSave();
            }
            if (Data.Shortcuts.Count == 0)
            {
                Data.Shortcuts.Add(new AppShortcut { Name = "Notatnik", ExePath = "notepad.exe", IconPath = "https://upload.wikimedia.org/wikipedia/commons/thumb/1/18/Notepad_icon.png/120px-Notepad_icon.png" });
                RequestSave();
            }
        }

        // NOWOŚĆ: Logika aplikowania zapisanego motywu
        private void ApplySavedTheme()
        {
            // 1. Aplikuj motyw bazowy (np. Dark, Cyberpunk)
            ThemeManager.ApplyTheme(Data.Config.CurrentTheme);

            // 2. Jeśli użytkownik ustawił własny akcent, nadpisz go
            if (!string.IsNullOrEmpty(Data.Config.CustomAccentColor))
            {
                ThemeManager.SetAccentColor(Data.Config.CustomAccentColor);
            }
        }

        // NOWOŚĆ: Logika zmiany motywu
        private void ChangeTheme(ThemeType theme)
        {
            ThemeManager.ApplyTheme(theme);

            Data.Config.CurrentTheme = theme;

            // Ważne: Gdy zmieniamy cały motyw (np. na Cyberpunk), resetujemy niestandardowy akcent,
            // ponieważ motyw narzuca własną paletę kolorów.
            Data.Config.CustomAccentColor = null;

            RequestSave();
        }

        private async void ProcessCheckTimer_Tick(object sender, EventArgs e)
        {
            var exePathsToCheck = Data.Shortcuts
                .Where(s => !s.IsSeparator && !s.IsSettings && !s.IsSubDock && !string.IsNullOrEmpty(s.ExePath))
                .Select(s => s.ExePath)
                .ToList();

            if (exePathsToCheck.Count == 0) return;

            await Task.Run(() =>
            {
                try
                {
                    var runningProcessNames = Process.GetProcesses()
                        .Select(p => p.ProcessName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (var shortcut in Data.Shortcuts)
                        {
                            if (shortcut.IsSeparator || shortcut.IsSettings || shortcut.IsSubDock) continue;

                            string exeName = Path.GetFileNameWithoutExtension(shortcut.ExePath);
                            if (string.IsNullOrEmpty(exeName)) continue;

                            bool isRunning = runningProcessNames.Contains(exeName);
                            if (shortcut.IsRunning != isRunning)
                            {
                                shortcut.IsRunning = isRunning;
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Błąd sprawdzania procesów: " + ex.Message);
                }
            });
        }

        public async Task HandleDroppedFiles(string[] files)
        {
            var newShortcuts = await Task.Run(() =>
            {
                var list = new List<AppShortcut>();
                foreach (string file in files)
                {
                    try
                    {
                        string resolvedPath = ShortcutHelper.ResolveShortcut(file);
                        string iconPath = IconHelper.ExtractIcon(resolvedPath);

                        list.Add(new AppShortcut
                        {
                            Name = Path.GetFileNameWithoutExtension(file),
                            ExePath = resolvedPath,
                            IconPath = iconPath,
                            IsSeparator = false
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Błąd dodawania pliku {file}: {ex.Message}");
                    }
                }
                return list;
            });

            foreach (var s in newShortcuts)
            {
                Data.Shortcuts.Add(s);
            }
            RequestSave();
        }

        public Thickness ItemMargin
        {
            get
            {
                int m = Data.Config.IconMargin;
                return Data.Config.Orientation == System.Windows.Controls.Orientation.Horizontal
                    ? new Thickness(m, 0, m, 0)
                    : new Thickness(0, m, 0, m);
            }
        }

        public Brush DockBackgroundBrush
        {
            get
            {
                if (Data.Config.EnableBlur) return new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                else
                {
                    byte alpha = (byte)(Math.Clamp(Data.Config.Opacity, 0.0, 1.0) * 255);
                    if (alpha == 0) alpha = 1;
                    return new SolidColorBrush(Color.FromArgb(alpha, 32, 32, 32));
                }
            }
        }

        private void Config_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            RequestSave();

            if (e.PropertyName == nameof(DockConfig.IconMargin) || e.PropertyName == nameof(DockConfig.Orientation))
            {
                OnPropertyChanged(nameof(ItemMargin));
            }
            if (e.PropertyName == nameof(DockConfig.Opacity) || e.PropertyName == nameof(DockConfig.EnableBlur))
            {
                OnPropertyChanged(nameof(DockBackgroundBrush));
            }
        }

        public void RequestSave()
        {
            SettingsManager.RequestSave(Data);
        }

        public void Cleanup()
        {
            _processCheckTimer?.Stop();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}