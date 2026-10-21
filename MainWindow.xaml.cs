using Qlyptra.Models;
using Qlyptra.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;

namespace Qlyptra;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly TrackSourceService _sourceService = new();
    private readonly YandexDownloadService _yandexDownloader = new();
    private readonly GlobalPasteMonitor _pasteMonitor = new();
    private HotkeyManager? _hotkeyManager;
    private WinForms.NotifyIcon? _trayIcon;
    private TrackInfo? _trackInfo;
    private string? _sourceAudioPath;
    private bool _sourceAudioTemporary;
    private string? _readyClipboardPath;
    private string? _lastSavedTrackPath;
    private bool _suppressSettingEvents;
    private bool _hotkeyCapture;
    private bool _closingReadyPopup;
    private bool _copyingTrack;
    private int _popupSecondsRemaining = 30;
    private int _currentPage; // 0 = converter, 1 = settings, 2 = about
    private const string DonateXUrl = "https://donatex.gg/donate/wz3k9l";
    private const string TelegramAuthorUrl = "https://t.me/wz3k9l";
    private readonly ConcurrentDictionary<string, byte> _pendingDeletion = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _scheduledDeletion = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FrameworkElement, ThemeColorSnapshot> _themeColorSnapshots = new();
    private DispatcherTimer? _readyTimer;
    private bool _deleteDelayComboOpen;
    private bool _isExiting;
    private bool _suppressAppearanceEvents;
    private bool _uiReady;
    private const int ReadyPopupTimeoutSeconds = 30;

    private sealed record ThemeColorSnapshot(string? Background, string? Foreground, string? BorderBrush);

    private static readonly Dictionary<string, string> DarkBackgroundToLight = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#111217"] = "#F5F6F8", ["#15161B"] = "#FFFFFF", ["#191A20"] = "#FFFFFF",
        ["#1A1B21"] = "#ECEEF2", ["#202128"] = "#E8EAF0", ["#22232A"] = "#FFFFFF",
        ["#292A32"] = "#E5E7ED", ["#2A2B33"] = "#E5E7ED", ["#34353D"] = "#D2D5DE",
        ["#34353E"] = "#D2D5DE", ["#393A44"] = "#D2D5DE", ["#2B2C34"] = "#D8DAE1",
        ["#42434C"] = "#BFC3CC", ["#4A4B56"] = "#C9CCD4",
        ["#3A3B44"] = "#D2D5DE", ["#3A3B46"] = "#D8DAE1", ["#2E303A"] = "#D8DAE1"
    };

    private static readonly Dictionary<string, string> LightBackgroundToDark = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#F5F6F8"] = "#111217", ["#FFFFFF"] = "#191A20", ["#ECEEF2"] = "#1A1B21",
        ["#E8EAF0"] = "#202128", ["#E5E7ED"] = "#292A32", ["#D2D5DE"] = "#34353D",
        ["#D8DAE1"] = "#2B2C34", ["#BFC3CC"] = "#42434C", ["#C9CCD4"] = "#4A4B56"
    };

    private static readonly Dictionary<string, string> DarkForegroundToLight = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#F5F5F7"] = "#252730", ["#F3F3F6"] = "#252730", ["#F0F0F3"] = "#252730",
        ["#ECECF0"] = "#252730", ["#FFFFFF"] = "#252730", ["#B8B9C2"] = "#6E727D",
        ["#B9BAC2"] = "#6E727D", ["#A6A7B0"] = "#646874", ["#9697A1"] = "#646874",
        ["#90919B"] = "#646874", ["#898A95"] = "#666A75", ["#858691"] = "#666A75",
        ["#8C8D97"] = "#666A75", ["#81828D"] = "#666A75", ["#777984"] = "#666A75",
        ["#747681"] = "#6A6D77", ["#737580"] = "#6A6D77", ["#666873"] = "#737782"
    };

    private static readonly Dictionary<string, string> LightForegroundToDark = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#252730"] = "#F3F3F6", ["#6E727D"] = "#B8B9C2", ["#646874"] = "#9697A1",
        ["#666A75"] = "#898A95", ["#6A6D77"] = "#747681", ["#737782"] = "#666873"
    };

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            TempFileCleaner.EnsureFolders();
            TempFileCleaner.CleanOldFiles();
            _pasteMonitor.PasteDetected += () => Dispatcher.BeginInvoke(new Action(SchedulePendingDeletion));
            try { _pasteMonitor.Start(); } catch { }
        }
        catch (Exception ex)
        {
            App.LogFatal("MainWindow.ctor", ex);
            MessageBox.Show(
                "Ошибка при создании окна:\n\n" + ex.Message + "\n\n" + ex,
                "Qlyptra", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
        _suppressSettingEvents = true;
        TopmostToggle.IsChecked = _settings.AlwaysOnTop;
        var startupTaskEnabled = StartupManager.IsEnabled();
        if (!startupTaskEnabled && _settings.LaunchAtStartup)
        {
            try
            {
                StartupManager.SetEnabled(true);
                startupTaskEnabled = StartupManager.IsEnabled();
            }
            catch (Exception ex)
            {
                _settings.LaunchAtStartup = false;
                MessageBox.Show("Автозапуск не удалось перенести на запуск с правами администратора:\n\n" + ex.Message,
                    "Qlyptra", MessageBoxButton.OK, MessageBoxImage.Warning);
                SaveSettingsQuietly();
            }
        }
        _settings.LaunchAtStartup = startupTaskEnabled;
        StartupToggle.IsChecked = _settings.LaunchAtStartup;
        
            _suppressAppearanceEvents = true;
            try
            {
                if (BrightnessSlider != null)
                {
                    BrightnessSlider.Value = Math.Clamp(_settings.BackgroundBrightness, 0.25, 1.0);
                    BrightnessValueLabel.Text = $"{_settings.BackgroundBrightness * 100:0}%";
                }
            }
            finally { _suppressAppearanceEvents = false; }

            HotkeyTextBox.Text = string.IsNullOrWhiteSpace(_settings.Hotkey) ? "Ctrl+Alt+M" : _settings.Hotkey;
        YandexTokenBox.Password = _settings.YandexToken;
        ThemeCombo.SelectedValue = NormalizeTheme(_settings.Theme);
        SetDeleteDelaySelection(_settings.DeleteDelaySeconds);
        CaptureThemeColors();
        LoadAppArtwork();
        _suppressSettingEvents = false;

        ApplyWindowSettings();
        _currentPage = 0;
        ApplyTheme(_settings.Theme);
        ApplyAccentAndBrightness();
        UpdateNavigationSelection(_currentPage);
        CreateTrayIcon();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        _readyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _readyTimer.Tick += ReadyTimer_Tick;

        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase)))
        {
            Hide();
            ShowInTaskbar = false;
        }
        _uiReady = true;
        }
        catch (Exception ex)
        {
            App.LogFatal("Window_Loaded", ex);
            MessageBox.Show("Ошибка при загрузке окна:\n\n" + ex.Message + "\n\nПодробности: %LOCALAPPDATA%\\Qlyptra\\startup.log",
                "Qlyptra", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _hotkeyManager = new HotkeyManager(this);
        _hotkeyManager.Initialize();
        _hotkeyManager.Activated += ToggleWindowVisibility;
        var success = _hotkeyManager.Register(_settings.Hotkey);
        if (!success) SetStatus("Горячая клавиша занята. Измените её в настройках.", false);
    }

    private void ApplyWindowSettings() => Topmost = _settings.AlwaysOnTop;

    private void CaptureThemeColors()
    {
        _themeColorSnapshots.Clear();
        foreach (var element in FindVisualChildren<FrameworkElement>(this))
            _themeColorSnapshots[element] = SnapshotColors(element);
    }

    private static ThemeColorSnapshot SnapshotColors(FrameworkElement element)
    {
        string? background = null;
        string? foreground = null;
        string? borderBrush = null;
        if (element is Control control)
        {
            background = BrushHex(control.Background);
            foreground = BrushHex(control.Foreground);
            borderBrush = BrushHex(control.BorderBrush);
        }
        else if (element is Border border)
        {
            background = BrushHex(border.Background);
            borderBrush = BrushHex(border.BorderBrush);
        }
        else if (element is TextBlock text)
        {
            foreground = BrushHex(text.Foreground);
        }
        return new ThemeColorSnapshot(background, foreground, borderBrush);
    }

    private static string? BrushHex(Brush? brush) => brush is SolidColorBrush solid
        ? (solid.Color.A == 255 ? ColorHex(solid.Color) : $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}")
        : null;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target) yield return target;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void CreateTrayIcon()
    {
        if (_trayIcon is not null) return;
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Открыть Qlyptra", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow)));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication)));
            var exe = Environment.ProcessPath;
            DrawingIcon? icon = null;
            if (!string.IsNullOrWhiteSpace(exe)) icon = DrawingIcon.ExtractAssociatedIcon(exe);
            _trayIcon = new WinForms.NotifyIcon
            {
                Text = "Qlyptra",
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        }
        catch { SetStatus("Не удалось создать значок в системном трее.", false); }
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) HideToTray();
        else ShowWindow();
    }

    private void ShowWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // The dash minimizes normally: the window remains represented on the taskbar and the tray icon stays available.
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    // The X button hides the window from the taskbar but keeps Qlyptra running in the system tray.
    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (_trayIcon is not null)
        {
            _trayIcon.BalloonTipTitle = "Qlyptra";
            _trayIcon.BalloonTipText = "Qlyptra работает в системном трее. Откройте окно двойным щелчком по значку или горячей клавишей.";
            _trayIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(2200);
        }
    }

    // Alt+F4 and other normal close requests also hide to tray. Exit remains available explicitly in the tray menu.
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        HideToTray();
    }

    private void SupportProject_Click(object sender, RoutedEventArgs e) => SwitchPage(2);

    private void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        _readyTimer?.Stop();
        DeleteCurrentSourceFile();
        _pasteMonitor.Dispose();
        _hotkeyManager?.Dispose();
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        Close();
        Application.Current.Shutdown();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _readyTimer?.Stop();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _pasteMonitor.Dispose();
        _hotkeyManager?.Dispose();
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme == "System")
            Dispatcher.BeginInvoke(new Action(() => { ApplyTheme("System"); ApplyAccentAndBrightness(); }));
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualAncestor<Button>(source) is not null) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T found) return found;
            DependencyObject? parent;
            try { parent = VisualTreeHelper.GetParent(element); }
            catch { break; }
            element = parent;
        }
        return null;
    }

    private async void ReadLink_Click(object sender, RoutedEventArgs e)
    {
        var text = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) { SetStatus("Сначала вставьте ссылку.", false); return; }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var inputUri) || (inputUri.Scheme != Uri.UriSchemeHttp && inputUri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("Вставьте корректную ссылку, начинающуюся с https://", false);
            return;
        }

        DeleteCurrentSourceFile();
        _trackInfo = null;
        _sourceAudioPath = null;
        _sourceAudioTemporary = false;
        TitleTextBox.Text = string.Empty;
        ArtistTextBox.Text = string.Empty;
        SourceFileLabel.Text = "Обрабатываю ссылку…";
        ShowCover(null);
        SetDownloadBusy(true);
        SetStatus("Проверяю ссылку и получаю информацию о треке…", null);

        try
        {
            var isYandex = inputUri.Host.Equals("music.yandex.ru", StringComparison.OrdinalIgnoreCase)
                || inputUri.Host.EndsWith(".music.yandex.ru", StringComparison.OrdinalIgnoreCase)
                || inputUri.Host.Equals("music.yandex.com", StringComparison.OrdinalIgnoreCase)
                || inputUri.Host.EndsWith(".music.yandex.com", StringComparison.OrdinalIgnoreCase);

            if (isYandex)
            {
                _trackInfo = await _sourceService.ReadYandexTrackAsync(text);
                _trackInfo.OriginalUrl = text;
                TitleTextBox.Text = _trackInfo.Title;
                ArtistTextBox.Text = _trackInfo.Artist;
                ShowCover(_trackInfo.CoverBytes);
                if (string.IsNullOrWhiteSpace(_settings.YandexToken))
                {
                    SourceFileLabel.Text = "Метаданные получены; токен не задан";
                    SetStatus("Метаданные получены. Чтобы скачать сам трек, добавьте токен Яндекс Музыки в настройках.", false);
                    return;
                }

                SetStatus("Скачиваю исходный трек и преобразую его в MP3 высокого качества…", null);
                var downloaded = await _yandexDownloader.DownloadTrackAsMp3Async(text, _settings.YandexToken, _trackInfo.Title, _trackInfo.Artist);
                _sourceAudioPath = downloaded.Mp3Path;
                _sourceAudioTemporary = true;
                if (string.IsNullOrWhiteSpace(_trackInfo.Title) || _trackInfo.Title.Equals("Трек Яндекс Музыки", StringComparison.OrdinalIgnoreCase))
                {
                    _trackInfo.Title = downloaded.Title;
                    TitleTextBox.Text = downloaded.Title;
                }
                if (string.IsNullOrWhiteSpace(_trackInfo.Artist) && !string.IsNullOrWhiteSpace(downloaded.Artist))
                {
                    _trackInfo.Artist = downloaded.Artist;
                    ArtistTextBox.Text = downloaded.Artist;
                }
                SourceFileLabel.Text = "Трек скачан и подготовлен как MP3";
                SetStatus("Трек готов. Подтверди в появившемся окне копирование MP3 или оригинальной ссылки.", true);
                ShowTrackReadyPopup();
            }
            else
            {
                SetStatus("Загружаю прямой аудиофайл…", null);
                _sourceAudioPath = await _sourceService.DownloadDirectMp3Async(text);
                _sourceAudioTemporary = true;
                var filename = Path.GetFileNameWithoutExtension(inputUri.AbsolutePath);
                var title = Uri.UnescapeDataString(filename.Replace('_', ' '));
                _trackInfo = new TrackInfo { OriginalUrl = text, Title = title, Artist = string.Empty };
                TitleTextBox.Text = title;
                ArtistTextBox.Text = string.Empty;
                ShowCover(null);
                SourceFileLabel.Text = "Прямой аудиофайл загружен";
                SetStatus("Аудиофайл скачан. Теперь его можно скопировать в мессенджер.", true);
                ShowTrackReadyPopup();
            }
        }
        catch (Exception ex)
        {
            SetStatus(ShortError(ex), false);
            SourceFileLabel.Text = "Не удалось скачать трек";
            DeleteCurrentSourceFile();
        }
        finally
        {
            SetDownloadBusy(false);
        }
    }

    private void SetDownloadBusy(bool busy)
    {
        DownloadButton.IsEnabled = !busy;
        DownloadButton.Content = busy ? "Скачивание…" : "Найти / скачать";
        DownloadProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
        if (busy) StartProgressAnimation();
        else
        {
            DownloadProgressTransform.BeginAnimation(TranslateTransform.XProperty, null);
            DownloadProgressTransform.X = -100;
        }
    }

    private void DownloadProgressBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DownloadProgressBar.Visibility == Visibility.Visible) StartProgressAnimation();
    }

    private void StartProgressAnimation()
    {
        if (DownloadProgressBar.ActualWidth < 1) return;
        var animation = new DoubleAnimation(-100, DownloadProgressBar.ActualWidth + 100, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        DownloadProgressTransform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ShowTrackReadyPopup()
    {
        if (_trackInfo is null || string.IsNullOrWhiteSpace(_sourceAudioPath) || !File.Exists(_sourceAudioPath)) return;
        _popupSecondsRemaining = ReadyPopupTimeoutSeconds;
        PopupTimerLabel.Text = "Временный файл будет очищен";
        ReadyTitleText.Text = string.IsNullOrWhiteSpace(_trackInfo.Title) ? "Без названия" : _trackInfo.Title;
        ReadyArtistText.Text = string.IsNullOrWhiteSpace(_trackInfo.Artist) ? "Исполнитель не указан" : _trackInfo.Artist;
        ReadyCopyAudioButton.IsEnabled = true;
        ReadyCopyAudioButton.Content = "Копировать MP3";
        if (ReadySaveButton != null)
        {
            ReadySaveButton.IsEnabled = true;
            ReadySaveButton.Content = "Сохранить";
        }
        if (ReadySaveStatus != null)
        {
            ReadySaveStatus.Text = "";
            ReadySaveStatus.Visibility = Visibility.Collapsed;
        }
        if (ReadyOpenFolderButton != null)
            ReadyOpenFolderButton.Visibility = Visibility.Collapsed;
        _lastSavedTrackPath = null;
        if (CoverImage.Source is BitmapSource coverBitmap)
            ReadyCoverImage.Source = coverBitmap;
        else
            ReadyCoverImage.Source = null;
        _closingReadyPopup = false;
        TrackReadyOverlay.Visibility = Visibility.Visible;
        TrackReadyOverlay.Opacity = 1;
        ReadyCard.Opacity = 1;
        _popupSecondsRemaining = ReadyPopupTimeoutSeconds;
        PopupTimerLabel.Text = "Скопируйте MP3 или сохраните трек";
        PopupTimerText.Text = "—";
        PopupTimerFill.Width = 0;
        _readyTimer?.Stop();
    }

    private void ReadyTimer_Tick(object? sender, EventArgs e)
    {
        if (TrackReadyOverlay.Visibility != Visibility.Visible || _closingReadyPopup) { _readyTimer?.Stop(); return; }
        if (_copyingTrack) return;
        _popupSecondsRemaining--;
        UpdatePopupTimerVisuals();
        if (_popupSecondsRemaining <= 0)
        {
            var clipboardWasPasted = !string.IsNullOrWhiteSpace(_readyClipboardPath) && _scheduledDeletion.ContainsKey(_readyClipboardPath);
            CancelReadyClipboardIfNotPasted();
            _ = DismissReadyPopupAsync(deleteSource: true,
                clipboardWasPasted ? "Окно готовности закрыто. Удаление копии продолжится по выбранной задержке." : "Время истекло. Временный файл удалён.",
                clipboardWasPasted);
        }
    }

    private void UpdatePopupTimerVisuals()
    {
        PopupTimerText.Text = $"{Math.Max(0, _popupSecondsRemaining)} сек";
        var width = PopupTimerTrack.ActualWidth;
        if (width <= 0) width = 370;
        PopupTimerFill.Width = width * Math.Clamp(_popupSecondsRemaining / (double)ReadyPopupTimeoutSeconds, 0, 1);
    }

    private async void ReadyCopyAudio_Click(object sender, RoutedEventArgs e)
    {
        var sourcePath = _sourceAudioPath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || _trackInfo is null)
        {
            await DismissReadyPopupAsync(true, "Временный аудиофайл не найден. Вставьте ссылку ещё раз.", false);
            return;
        }
        if (!Path.GetExtension(sourcePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Загрузчик вернул файл, который не распознан как MP3.", false);
            return;
        }

        _readyTimer?.Stop();
        _copyingTrack = true;
        ReadyCopyAudioButton.IsEnabled = false;
        ReadyCopyLinkButton.IsEnabled = false;
        if (ReadySaveButton != null) ReadySaveButton.IsEnabled = false;
        ReadyCloseButton.IsEnabled = false;
        ReadyCopyAudioButton.Content = "Подготовка…";
        string? outputPath = null;
        try
        {
            TempFileCleaner.EnsureFolders();
            var title = string.IsNullOrWhiteSpace(TitleTextBox.Text) ? _trackInfo.Title : TitleTextBox.Text.Trim();
            var artist = ArtistTextBox.Text.Trim();
            var safeName = MakeSafeFileName(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}");
            outputPath = Path.Combine(TempFileCleaner.OutboxFolder, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..5]}.mp3");
            await Task.Run(() => Id3Writer.CreateTaggedCopy(sourcePath, outputPath, title, artist, _trackInfo.CoverBytes));
            var paths = new System.Collections.Specialized.StringCollection();
            paths.Add(outputPath);
            Clipboard.SetFileDropList(paths);

            // Источник оставляем: его можно ещё сохранить на диск из этого же окна.
            _pendingDeletion.TryAdd(outputPath, 0);
            _readyClipboardPath = outputPath;
            _pasteMonitor.Arm();
            _ = DeleteClipboardIfNotPastedAsync(outputPath, TimeSpan.FromSeconds(ReadyPopupTimeoutSeconds));
            _popupSecondsRemaining = ReadyPopupTimeoutSeconds;
            PopupTimerLabel.Text = "До автоматического закрытия окна";
            ReadyCopyAudioButton.IsEnabled = false;
            ReadyCopyAudioButton.Content = "Скопировано ✓";
            // Таймер один раз: не сбрасывать при повторном копировании
            if (_readyTimer is null || !_readyTimer.IsEnabled)
            {
                _popupSecondsRemaining = ReadyPopupTimeoutSeconds;
                PopupTimerLabel.Text = "Временный файл будет очищен";
                UpdatePopupTimerVisuals();
                _readyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _readyTimer.Tick -= ReadyTimer_Tick;
                _readyTimer.Tick += ReadyTimer_Tick;
                _readyTimer.Start();
            }
            SetStatus("MP3 в буфере обмена. Вставьте файл в любой мессенджер (Ctrl+V) или сохраните трек на диск.", true);
        }
        catch (Exception ex)
        {
            if (outputPath is not null && !_pendingDeletion.ContainsKey(outputPath)) TryDeleteFile(outputPath);
            SetStatus("Не удалось скопировать MP3: " + ShortError(ex), false);
            ReadyCopyAudioButton.IsEnabled = true;
            ReadyCopyAudioButton.Content = "Повторить копирование";
            _readyTimer?.Start();
        }
        finally
        {
            _copyingTrack = false;
            ReadyCopyLinkButton.IsEnabled = true;
            if (ReadySaveButton != null) ReadySaveButton.IsEnabled = true;
            ReadyCloseButton.IsEnabled = true;
        }
    }

    private async void ReadySave_Click(object sender, RoutedEventArgs e)
    {
        if (_copyingTrack || _trackInfo is null) return;

        var sourcePath = _sourceAudioPath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            // После копирования источник мог остаться; иначе пробуем файл из буфера.
            if (!string.IsNullOrWhiteSpace(_readyClipboardPath) && File.Exists(_readyClipboardPath))
                sourcePath = _readyClipboardPath;
            else
            {
                SetStatus("Файл трека недоступен. Загрузите ссылку ещё раз.", false);
                return;
            }
        }

        ReadySaveButton.IsEnabled = false;
        ReadySaveButton.Content = "Сохранение…";
        try
        {
            var title = string.IsNullOrWhiteSpace(TitleTextBox.Text) ? _trackInfo.Title : TitleTextBox.Text.Trim();
            var artist = ArtistTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(artist)) artist = _trackInfo.Artist;
            var primary = TrackLibrary.GetPrimaryArtist(artist);
            var savePath = TrackLibrary.BuildSavePath(primary, artist, title);

            await Task.Run(() => Id3Writer.CreateTaggedCopy(sourcePath!, savePath, title, artist, _trackInfo.CoverBytes));
            _lastSavedTrackPath = savePath;

            ReadySaveStatus.Text = "Сохранено: " + savePath;
            ReadySaveStatus.Visibility = Visibility.Visible;
            ReadyOpenFolderButton.Visibility = Visibility.Visible;
            ReadySaveButton.Content = "Сохранено ✓";
            if (_readyTimer == null || !_readyTimer.IsEnabled)
            {
                _popupSecondsRemaining = ReadyPopupTimeoutSeconds;
                PopupTimerLabel.Text = "Окно закроется автоматически";
                UpdatePopupTimerVisuals();
                _readyTimer?.Stop();
                _readyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _readyTimer.Tick -= ReadyTimer_Tick;
                _readyTimer.Tick += ReadyTimer_Tick;
                _readyTimer.Start();
            }
            SetStatus("Трек сохранён: " + savePath, true);
        }
        catch (Exception ex)
        {
            ReadySaveButton.IsEnabled = true;
            ReadySaveButton.Content = "Сохранить";
            SetStatus("Не удалось сохранить трек: " + ShortError(ex), false);
        }
    }

    private void ReadyOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _lastSavedTrackPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                return;
            }
            if (Directory.Exists(TrackLibrary.RootFolder))
                Process.Start(new ProcessStartInfo("explorer.exe", TrackLibrary.RootFolder) { UseShellExecute = true });
        }
        catch { SetStatus("Не удалось открыть папку.", false); }
    }

    private async void ReadyCopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_copyingTrack) return;
        var link = _trackInfo?.OriginalUrl;
        if (string.IsNullOrWhiteSpace(link)) link = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(link))
        {
            await DismissReadyPopupAsync(true, "Не найдена оригинальная ссылка на трек.", false);
            return;
        }
        try
        {
            var clipboardWasPasted = !string.IsNullOrWhiteSpace(_readyClipboardPath) && _scheduledDeletion.ContainsKey(_readyClipboardPath);
            if (!clipboardWasPasted) CancelReadyClipboardIfNotPasted();
            Clipboard.SetText(link);
            await DismissReadyPopupAsync(deleteSource: true,
                clipboardWasPasted ? "Оригинальная ссылка скопирована. Удаление вставленной копии продолжится по выбранной задержке." : "Оригинальная ссылка скопирована. Не вставленная временная копия удалена.",
                true);
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось скопировать ссылку: " + ShortError(ex), false);
        }
    }

    private async void ReadyClose_Click(object sender, RoutedEventArgs e)
    {
        if (_copyingTrack) return;
        var alreadyPasted = !string.IsNullOrWhiteSpace(_readyClipboardPath) && _scheduledDeletion.ContainsKey(_readyClipboardPath);
        CancelReadyClipboardIfNotPasted();
        await DismissReadyPopupAsync(deleteSource: true,
            alreadyPasted ? "Окно закрыто. Удаление MP3 продолжится по выбранной задержке." : "Окно закрыто. Временный аудиофайл удалён.",
            alreadyPasted);
    }

    private void CancelReadyClipboardIfNotPasted()
    {
        var path = _readyClipboardPath;
        _readyClipboardPath = null;
        if (string.IsNullOrWhiteSpace(path) || _scheduledDeletion.ContainsKey(path)) return;
        TryDeleteFile(path);
        _pendingDeletion.TryRemove(path, out _);
        _scheduledDeletion.TryRemove(path, out _);
        if (_pendingDeletion.IsEmpty) _pasteMonitor.Disarm();
    }

    private async Task DismissReadyPopupAsync(bool deleteSource, string statusText, bool? success)
    {
        if (_closingReadyPopup || _copyingTrack) return;
        _closingReadyPopup = true;
        _readyTimer?.Stop();
        if (deleteSource) DeleteCurrentSourceFile();

        TrackReadyOverlay.Visibility = Visibility.Collapsed;
        TrackReadyOverlay.Opacity = 0;
        ReadyCard.Opacity = 0;
        UrlTextBox.Clear();
        TitleTextBox.Clear();
        ArtistTextBox.Clear();
        SourceFileLabel.Text = "Ожидание ссылки на трек";
        ShowCover(null);
        _trackInfo = null;
        _sourceAudioPath = null;
        _sourceAudioTemporary = false;
        _readyClipboardPath = null;
        SetStatus(statusText, success);
        _closingReadyPopup = false;
    }

    private async Task DeleteClipboardIfNotPastedAsync(string path, TimeSpan timeout)
    {
        await Task.Delay(timeout);
        if (!_pendingDeletion.ContainsKey(path) || _scheduledDeletion.ContainsKey(path)) return;
        var deleted = TryDeleteFile(path);
        _pendingDeletion.TryRemove(path, out _);
        _scheduledDeletion.TryRemove(path, out _);
        if (_pendingDeletion.IsEmpty) _pasteMonitor.Disarm();
        await Dispatcher.InvokeAsync(() => SetStatus(
            deleted ? "Временная копия удалена: вставка не была обнаружена за 30 секунд." : "Временная копия будет очищена после освобождения файла.", deleted));
    }

    private void SchedulePendingDeletion()
    {
        var current = _pendingDeletion.Keys.ToArray();
        if (current.Length == 0) { _pasteMonitor.Disarm(); return; }
        foreach (var path in current)
            if (_scheduledDeletion.TryAdd(path, 0)) _ = DeleteAfterDelayAsync(path, _settings.DeleteDelaySeconds);
    }

    private async Task DeleteAfterDelayAsync(string path, int delay)
    {
        var deleted = false;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay, 10, 180)));
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (!File.Exists(path)) { deleted = true; break; }
                try
                {
                    File.Delete(path);
                    deleted = !File.Exists(path);
                    if (deleted) break;
                }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        catch { }
        finally
        {
            _pendingDeletion.TryRemove(path, out _);
            _scheduledDeletion.TryRemove(path, out _);
            if (_pendingDeletion.IsEmpty) _pasteMonitor.Disarm();
        }
        try
        {
            await Dispatcher.InvokeAsync(() => SetStatus(
                deleted ? "Временная копия после вставки удалена. Исходный файл не изменялся." : "Не удалось удалить временную копию сразу; она будет очищена при следующем запуске.", deleted));
        }
        catch { }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch { return false; }
    }

    private void DeleteCurrentSourceFile()
    {
        if (_sourceAudioTemporary && !string.IsNullOrWhiteSpace(_sourceAudioPath))
            TryDeleteFile(_sourceAudioPath);
        _sourceAudioPath = null;
        _sourceAudioTemporary = false;
    }

    private void ShowCover(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            CoverImage.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
            ReadyCoverImage.Source = null;
            return;
        }
        try
        {
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            CoverImage.Source = bitmap;
            CoverPlaceholder.Visibility = Visibility.Collapsed;
            ReadyCoverImage.Source = bitmap;
        }
        catch
        {
            CoverImage.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
            ReadyCoverImage.Source = null;
        }
    }

    private static string MakeSafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 100) name = name[..100];
        return string.IsNullOrWhiteSpace(name) ? "Qlyptra-track" : name;
    }

    private static string ShortError(Exception exception)
    {
        var message = exception is AggregateException aggregate ? aggregate.GetBaseException().Message : exception.Message;
        return message.Length > 230 ? message[..230] + "…" : message;
    }


    private void TelegramAuthor_Click(object sender, RoutedEventArgs e)
    {
        OpenProjectLink(TelegramAuthorUrl, "Не удалось открыть Telegram автора.");
    }

    private void ProjectLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenProjectLink(e.Uri.AbsoluteUri, null);
        e.Handled = true;
    }

    private void DonateX_Click(object sender, RoutedEventArgs e) => OpenProjectLink(DonateXUrl, null);

    private static void OpenProjectLink(string url, string? missingMessage)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(missingMessage ?? "Ссылка не настроена.", "Qlyptra", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Не удалось открыть ссылку: " + ex.Message, "Qlyptra", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void TokenLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Не удалось открыть страницу токена: " + ShortError(ex), false); }
        e.Handled = true;
    }

    private void SaveYandexToken_Click(object sender, RoutedEventArgs e)
    {
        var token = YandexTokenBox.Password.Trim();
        _settings.YandexToken = token;
        try
        {
            SettingsStore.Save(_settings);
            SetStatus(string.IsNullOrWhiteSpace(token) ? "Токен Яндекс Музыки удалён из настроек." : "Токен сохранён в зашифрованном виде. Вставьте ссылку и нажмите «Найти / скачать».", true);
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось сохранить токен: " + ShortError(ex), false);
        }
    }

    private void ThemeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents || ThemeCombo.SelectedValue is null) return;
        _settings.Theme = NormalizeTheme(ThemeCombo.SelectedValue.ToString());
        ApplyTheme(_settings.Theme);
        ApplyAccentAndBrightness();
        SaveSettingsQuietly();
    }

    private static string NormalizeTheme(string? mode) => mode is "Dark" or "Light" or "System" ? mode : "System";

    private void ApplyTheme(string mode)
    {
        var light = mode == "Light" || (mode == "System" && IsWindowsLightTheme());

        // Theme palette is refreshed through DynamicResource, so control templates keep
        // their hover/pressed triggers instead of being overwritten with local brush values.
        SetResourceBrush("WindowBrush", light ? "#F5F5F5" : "#111111");
        SetResourceBrush("ChromeBrush", light ? "#FFFFFF" : "#151515");
        SetResourceBrush("PanelBrush", light ? "#FFFFFF" : "#191919");
        SetResourceBrush("SubtleBrush", light ? "#ECECEC" : "#1B1B1B");
        SetResourceBrush("CoverBrush", light ? "#E8E8E8" : "#202020");
        SetResourceBrush("InputBrush", light ? "#FFFFFF" : "#232323");
        SetResourceBrush("HoverBrush", light ? "#E5E5E5" : "#292929");
        SetResourceBrush("ButtonBrush", light ? "#E5E5E5" : "#2A2A2A");
        SetResourceBrush("BorderBrushCustom", light ? "#D2D2D2" : "#353535");
        SetResourceBrush("MainTextBrush", light ? "#272727" : "#F3F3F3");
        SetResourceBrush("MutedTextBrush", light ? "#666666" : "#969696");
        SetResourceBrush("SelectedItemBrush", light ? "#D8D8D8" : "#353535");

        foreach (var element in FindVisualChildren<FrameworkElement>(this))
        {
            // Never assign local values to template-generated visuals: doing so outranks
            // ControlTemplate triggers and was the reason hover effects appeared broken.
            if (element.TemplatedParent is not null) continue;

            if (!_themeColorSnapshots.TryGetValue(element, out var snapshot))
            {
                snapshot = SnapshotColors(element);
                _themeColorSnapshots[element] = snapshot;
            }

            // Controls take their foreground/background from DynamicResource-based styles.
            // Do not set local values here: local values would override hover/pressed triggers.
            // Surface colours are DynamicResource-based and must remain bound to the
            // shared palette. Only explicit TextBlock foregrounds are mapped for Light mode.
            if (element is TextBlock text && snapshot.Foreground is not null
                && text.ReadLocalValue(TextBlock.ForegroundProperty) != DependencyProperty.UnsetValue)
            {
                text.Foreground = BrushFrom(snapshot.Foreground.Equals("#F04452", StringComparison.OrdinalIgnoreCase)
                    ? GetThemeColor("AccentBrush", "#F04452")
                    : MapForeground(snapshot.Foreground, light));
            }
        }

        UpdateNavigationSelection(_currentPage);
    }

    private static string MapBackground(string original, bool light)
    {
        if (light)
            return DarkBackgroundToLight.TryGetValue(original, out var lightBackground) ? lightBackground : original;
        return LightBackgroundToDark.TryGetValue(original, out var darkBackground) ? darkBackground : original;
    }

    private static string MapForeground(string original, bool light)
    {
        var mapped = light
            ? (DarkForegroundToLight.TryGetValue(original, out var lightForeground) ? lightForeground : original)
            : (LightForegroundToDark.TryGetValue(original, out var darkForeground) ? darkForeground : original);
        // Neutralise the slight blue/purple cast in muted labels without altering saturated
        // semantic colours (the Qlyptra red accent, green status and similar indicators).
        if (TryParseOpaqueColor(mapped, out var color))
        {
            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            var min = Math.Min(color.R, Math.Min(color.G, color.B));
            if (max - min <= 48)
            {
                var gray = (byte)Math.Clamp((int)Math.Round(color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722), 0, 255);
                return ColorHex(Color.FromRgb(gray, gray, gray));
            }
        }
        return mapped;
    }

    private static string MapBorder(string original, bool light)
    {
        if (light)
        {
            if (DarkBackgroundToLight.TryGetValue(original, out var lightBorder)) return lightBorder;
            if (original.Equals("#34353E", StringComparison.OrdinalIgnoreCase)) return "#D2D5DE";
            return original;
        }
        return LightBackgroundToDark.TryGetValue(original, out var darkBorder) ? darkBorder : original;
    }

    private static void SetResourceBrush(string key, string hex)
    {
        if (Application.Current is not null) Application.Current.Resources[key] = BrushFrom(hex);
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 0)) != 0;
        }
        catch { return false; }
    }

    private static string ColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static SolidColorBrush BrushFrom(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void LoadAppArtwork()
    {
        try
        {
            BitmapImage? bitmap = null;
            // Сначала встроенный ресурс — файл не лежит рядом с exe.
            try
            {
                var packUri = new Uri("pack://application:,,,/Assets/app-art.png");
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = packUri;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch
            {
                bitmap = null;
            }

            if (bitmap is null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app-art.png");
                if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "app-art.png");
                if (!File.Exists(path)) return;
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
            }

            AppArtworkImage.Source = bitmap;
            AppArtworkImage.Visibility = Visibility.Visible;
            ArtworkFallback.Visibility = Visibility.Collapsed;

        }
        catch { }
    }

    private void SetStatus(string text, bool? success)
    {
        StatusText.Text = text;
        StatusDot.Background = success == true
            ? new SolidColorBrush(Color.FromRgb(72, 198, 130))
            : success == false
                ? new SolidColorBrush(Color.FromRgb(240, 68, 82))
                : new SolidColorBrush(Color.FromRgb(140, 141, 152));
    }


    private void SwitchPage(int page)
    {
        var target = page switch { 1 => SettingsView, 2 => AboutView, _ => ConverterView };
        var current = _currentPage switch { 1 => SettingsView, 2 => AboutView, _ => ConverterView };
        if (_currentPage == page && target.Visibility == Visibility.Visible)
        {
            UpdateNavigationSelection(page);
            return;
        }

        _currentPage = page;
        UpdateNavigationSelection(page);

        current.Visibility = Visibility.Collapsed;
        current.Opacity = 0;
        current.IsHitTestVisible = false;

        target.Visibility = Visibility.Visible;
        target.Opacity = 1;
        target.IsHitTestVisible = true;
        RenderOptions.SetBitmapScalingMode(target, BitmapScalingMode.HighQuality);
    }


    private void ConverterNav_Click(object sender, RoutedEventArgs e) => SwitchPage(0);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => SwitchPage(1);
    private void AboutNav_Click(object sender, RoutedEventArgs e) => SwitchPage(2);

    private void UpdateNavigationSelection(int page)
    {
        SetNavigationButton(ConverterNav, isActive: page == 0);
        SetNavigationButton(SettingsNav, isActive: page == 1);
        SetNavigationButton(AboutNav, isActive: page == 2);
    }

    private void SetNavigationButton(Button button, bool isActive)
    {
        button.Tag = isActive ? "Active" : "Inactive";
        button.Background = isActive
            ? BrushFrom(GetThemeColor("SelectedItemBrush", "#34353D"))
            : Brushes.Transparent;
        button.BorderBrush = isActive
            ? BrushFrom(GetThemeColor("AccentBrush", "#F04452"))
            : Brushes.Transparent;
        button.BorderThickness = new Thickness(1);
        button.Foreground = BrushFrom(GetThemeColor(isActive ? "MainTextBrush" : "MutedTextBrush", isActive ? "#F3F3F6" : "#9697A1"));
    }

    private static string GetThemeColor(string key, string fallback)
    {
        if (Application.Current?.Resources[key] is SolidColorBrush brush) return ColorHex(brush.Color);
        return fallback;
    }

    private void TopmostToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents || TopmostToggle.IsChecked is null) return;
        _settings.AlwaysOnTop = TopmostToggle.IsChecked.Value;
        Topmost = _settings.AlwaysOnTop;
        SaveSettingsQuietly();
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents || StartupToggle.IsChecked is null) return;
        try
        {
            StartupManager.SetEnabled(StartupToggle.IsChecked.Value);
            _settings.LaunchAtStartup = StartupToggle.IsChecked.Value;
            SaveSettingsQuietly();
            SetStatus(_settings.LaunchAtStartup ? "Автозапуск включён." : "Автозапуск выключен.", true);
        }
        catch (Exception ex)
        {
            _suppressSettingEvents = true;
            StartupToggle.IsChecked = StartupManager.IsEnabled();
            _suppressSettingEvents = false;
            SetStatus("Не удалось изменить автозапуск: " + ShortError(ex), false);
        }
    }

    private void HotkeyTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _hotkeyCapture = true;
        HotkeyTextBox.Text = "Нажмите сочетание клавиш…";
    }

    private void HotkeyTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _hotkeyCapture = false;
        HotkeyTextBox.Text = string.IsNullOrWhiteSpace(_settings.Hotkey) ? "Ctrl+Alt+M" : _settings.Hotkey;
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_hotkeyCapture) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _hotkeyCapture = false;
            HotkeyTextBox.Text = string.IsNullOrWhiteSpace(_settings.Hotkey) ? "Ctrl+Alt+M" : _settings.Hotkey;
            Keyboard.ClearFocus();
            SetStatus("Изменение горячей клавиши отменено.", true);
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab) return;
        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        if (parts.Count == 0) { SetStatus("Задайте сочетание из модификатора и клавиши (например Ctrl и буква).", false); return; }
        parts.Add(key.ToString());
        var candidate = string.Join("+", parts);
        if (string.Equals(candidate, _settings.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            HotkeyTextBox.Text = candidate;
            Keyboard.ClearFocus();
            SetStatus("Горячая клавиша без изменений: " + candidate, true);
            return;
        }
        if (_hotkeyManager?.Register(candidate) == true)
        {
            _settings.Hotkey = candidate;
            HotkeyTextBox.Text = candidate;
            SaveSettingsQuietly();
            SetStatus("Горячая клавиша сохранена: " + candidate, true);
            Keyboard.ClearFocus();
        }
        else
        {
            _hotkeyManager?.Register(_settings.Hotkey);
            HotkeyTextBox.Text = string.IsNullOrWhiteSpace(_settings.Hotkey) ? "Ctrl+Alt+M" : _settings.Hotkey;
            SetStatus("Это сочетание недоступно или уже занято. Попробуй другое.", false);
            Keyboard.ClearFocus();
        }
    }

    private void DeleteDelayCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents || DeleteDelayCombo.SelectedValue is null) return;
        if (int.TryParse(DeleteDelayCombo.SelectedValue.ToString(), out var seconds))
        {
            _settings.DeleteDelaySeconds = seconds;
            SaveSettingsQuietly();
        }
    }

    private void SetDeleteDelaySelection(int seconds)
    {
        foreach (var item in DeleteDelayCombo.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var value) && value == seconds)
            {
                DeleteDelayCombo.SelectedItem = item;
                return;
            }
        }
        DeleteDelayCombo.SelectedIndex = 2;
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_deleteDelayComboOpen || DeleteDelayCombo.IsDropDownOpen || ThemeCombo.IsDropDownOpen)
        {
            e.Handled = true;
            return;
        }

        // Обычная прокрутка без сглаживания
        var offset = SettingsScrollViewer.VerticalOffset - e.Delta / 3.0;
        SettingsScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, SettingsScrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private void DeleteDelayCombo_DropDownOpened(object sender, EventArgs e)
    {
        _deleteDelayComboOpen = true;
    }

    private void DeleteDelayCombo_DropDownClosed(object sender, EventArgs e)
    {
        _deleteDelayComboOpen = false;
    }




    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressAppearanceEvents || _suppressSettingEvents) return;
        _settings.BackgroundBrightness = Math.Clamp(e.NewValue, 0.25, 1.0);
        if (BrightnessValueLabel != null)
            BrightnessValueLabel.Text = $"{_settings.BackgroundBrightness * 100:0}%";
        ApplyAccentAndBrightness();
        SaveSettingsQuietly();
    }

    private void ApplyAccentAndBrightness()
    {
        // Fixed Qlyptra accent. The user can adjust background brightness, not the accent hue.
        const string accentHex = "#F04452";
        const string accentHoverHex = "#B92F3A";
        SetResourceBrush("AccentBrush", accentHex);
        SetResourceBrush("AccentHoverBrush", accentHoverHex);

        var brightness = Math.Clamp(_settings.BackgroundBrightness, 0.25, 1.0);
        var mode = _settings.Theme;
        var light = mode == "Light" || (mode == "System" && IsWindowsLightTheme());

        string ScaledGray(int level)
        {
            // In dark mode 100% preserves the charcoal palette; lower values deepen it
            // without collapsing panels and borders into pure black. In light mode the
            // same slider lowers white/gray surfaces to a readable neutral gray.
            var adjusted = light
                ? 112 + (level - 112) * brightness
                : 8 + (level - 8) * brightness;
            var channel = (byte)Math.Clamp((int)Math.Round(adjusted), 0, 255);
            return ColorHex(Color.FromRgb(channel, channel, channel));
        }

        SetResourceBrush("WindowBrush", ScaledGray(light ? 246 : 17));
        SetResourceBrush("ChromeBrush", ScaledGray(light ? 255 : 21));
        SetResourceBrush("PanelBrush", ScaledGray(light ? 250 : 25));
        SetResourceBrush("SubtleBrush", ScaledGray(light ? 236 : 27));
        SetResourceBrush("CoverBrush", ScaledGray(light ? 232 : 32));
        SetResourceBrush("InputBrush", ScaledGray(light ? 250 : 34));
        SetResourceBrush("HoverBrush", ScaledGray(light ? 222 : 48));
        SetResourceBrush("ButtonBrush", ScaledGray(light ? 229 : 42));
        SetResourceBrush("BorderBrushCustom", ScaledGray(light ? 205 : 53));
        SetResourceBrush("SelectedItemBrush", ScaledGray(light ? 214 : 58));
        SetResourceBrush("MainTextBrush", light ? "#272727" : "#F3F3F3");
        SetResourceBrush("MutedTextBrush", light ? "#555555" : "#969696");

        foreach (var element in FindVisualChildren<FrameworkElement>(this))
        {
            if (element.TemplatedParent is not null) continue;
            if (!_themeColorSnapshots.TryGetValue(element, out var snapshot)) continue;

            // Neutral surfaces use DynamicResource, so changing brightness updates them
            // consistently without replacing resource bindings with local brushes.
            if (element is TextBlock text && snapshot.Foreground is not null
                && snapshot.Foreground.Equals("#F04452", StringComparison.OrdinalIgnoreCase)
                && text.ReadLocalValue(TextBlock.ForegroundProperty) != DependencyProperty.UnsetValue)
            {
                text.Foreground = BrushFrom(accentHex);
            }
        }

        SetResourceBrush("BorderBrushCustom", ScaledGray(light ? 205 : 53));
        // Main window surfaces, settings cards, cover placeholders, progress bars and
        // popup panels remain bound to the resources above and react to brightness live.
        UpdateNavigationSelection(_currentPage);
    }

    private static string AdjustNeutralSurface(string colorHex, bool light, double brightness)
    {
        if (!TryParseOpaqueColor(colorHex, out var color)) return colorHex;
        // All non-accent surfaces are grayscale, even when the original XAML value had
        // a subtle blue/lilac cast. Brightness moves them within the selected theme range.
        var level = color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722;
        var adjusted = light
            ? 112 + (level - 112) * brightness
            : 8 + (level - 8) * brightness;
        var channel = (byte)Math.Clamp((int)Math.Round(adjusted), 0, 255);
        return ColorHex(Color.FromRgb(channel, channel, channel));
    }

    private static bool TryParseOpaqueColor(string hex, out Color color)
    {
        try
        {
            if (hex.Length == 7 && hex[0] == '#')
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
                return true;
            }
        }
        catch { }
        color = Colors.Transparent;
        return false;
    }

    private void SaveSettingsQuietly()
    {
        try { SettingsStore.Save(_settings); }
        catch { SetStatus("Не удалось сохранить настройки.", false); }
    }
}
