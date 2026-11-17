using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Windows.System;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.LogParse;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Http;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Platform.Wine;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.I18n;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.View;
using BetterGenshinImpact.View.Controls.Webview;
using BetterGenshinImpact.View.Converters;
using BetterGenshinImpact.View.Pages;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Win32;
using Wpf.Ui;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class CommonSettingsPageViewModel : ViewModel
{
    private readonly IConfigService _configService;
    private readonly INavigationService _navigationService;

    private readonly NotificationService _notificationService;
    private readonly CustomHtmlMaskService _customHtmlMaskService;
    private readonly RecognitionTemplateEditorService _recognitionTemplateEditorService;
    private readonly I18nService _i18nService;
    private readonly TpConfig _tpConfig = TaskContext.Instance().Config.TpConfig;

    private string _selectedArea = string.Empty;


    private string _selectedCountry = string.Empty;
    [ObservableProperty] private List<string> _adventurersGuildCountry = ["无", "枫丹", "稻妻", "璃月", "蒙德"];
    
    [ObservableProperty] private List<Tuple<TimeSpan, string>> _serverTimeZones =
    [
        Tuple.Create(TimeSpan.FromHours(8), "Asia / CN / TW (UTC+08)"),
        Tuple.Create(TimeSpan.FromHours(1), "Europe (UTC+01)"),
        Tuple.Create(TimeSpan.FromHours(-5), "America (UTC-05)")
    ];

    public IReadOnlyDictionary<ItemIconRecognitionMode, string> ItemIconRecognitionModes { get; } =
        new Dictionary<ItemIconRecognitionMode, string>
        {
            [ItemIconRecognitionMode.Item] = "ItemV2",
            [ItemIconRecognitionMode.GridIcon] = "GridIcon"
        };

    public CommonSettingsPageViewModel(IConfigService configService, INavigationService navigationService,
        NotificationService notificationService, CustomHtmlMaskService customHtmlMaskService,
        RecognitionTemplateEditorService recognitionTemplateEditorService, I18nService i18nService)
    {
        Config = configService.Get();
        _configService = configService;
        Config.MaskWindowConfig.EnsureOverlayMetricItems();
        Config.MaskWindowConfig.MigrateLegacyOverlayMetricsLayout();
        _navigationService = navigationService;
        _notificationService = notificationService;
        _customHtmlMaskService = customHtmlMaskService;
        _recognitionTemplateEditorService = recognitionTemplateEditorService;
        _i18nService = i18nService;
        // 设置页需要可绑定对象，避免把 Dictionary<string, bool> 直接暴露给 XAML 并丢失固定枚举顺序。
        OverlayMetricItems = new ObservableCollection<OverlayMetricSettingItem>(
            OverlayMetricItemDefaults.AllItems.Select(item => new OverlayMetricSettingItem(Config.MaskWindowConfig, item, OnRefreshMaskSettings)));
        OverlayStyleSettingGroups = OverlayStyleSettingGroup.Create(Config.MaskWindowConfig, OnOverlayStyleChanged);
        InitializeCountries();
        InitializeMiyousheCookie();
        // 初始化OCR模型选择
        SelectedPaddleOcrModelConfig = Config.OtherConfig.OcrConfig.PaddleOcrModelConfig;
    }

    public AllConfig Config { get; set; }
    public ObservableCollection<OverlayMetricSettingItem> OverlayMetricItems { get; }
    public ObservableCollection<OverlayStyleSettingGroup> OverlayStyleSettingGroups { get; }
    public ObservableCollection<string> CountryList { get; } = new();
    public ObservableCollection<string> Areas { get; } = new();

    public ObservableCollection<string> MapPathingTypes { get; } = ["SIFT", "TemplateMatch"];

    [ObservableProperty] private FrozenDictionary<string, string> _languageDict =
        new[] { "en" }
            .ToFrozenDictionary(c => c, c => CultureInfoNameToKVPConverter.GetDisplayName(c));

    public string SelectedCountry
    {
        get => _selectedCountry;
        set
        {
            if (SetProperty(ref _selectedCountry, value))
            {
                UpdateAreas(value);
                SelectedArea = Areas.FirstOrDefault() ?? string.Empty;
            }
        }
    }

    public string SelectedArea
    {
        get => _selectedArea;
        set
        {
            if (SetProperty(ref _selectedArea, value))
            {
                UpdateRevivePoint(SelectedCountry, SelectedArea);
            }
        }
    }

    public ObservableCollection<PaddleOcrModelConfig> PaddleOcrModelConfigs { get; } =
        new(Enum.GetValues(typeof(PaddleOcrModelConfig)).Cast<PaddleOcrModelConfig>());

    [ObservableProperty] private PaddleOcrModelConfig _selectedPaddleOcrModelConfig;

    [RelayCommand]
    public void OnQuestionButtonOnClick()
    {
        //            Owner = this,
        WebpageWindow cookieWin = new()
        {
            Title = "Log Analysis",
            Width = 800,
            Height = 600,

            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        cookieWin.NavigateToHtml(TravelsDiaryDetailManager.generHtmlMessage());
        cookieWin.Show();
    }

    private void InitializeMiyousheCookie()
    {
        OtherConfig.Miyoushe mcfg = TaskContext.Instance().Config.OtherConfig.MiyousheConfig;
        if (mcfg.Cookie == string.Empty &&
            mcfg.LogSyncCookie)
        {
            var config = LogParse.LoadConfig();
            mcfg.Cookie = config.Cookie;
        }
    }

    private void InitializeCountries()
    {
        var countries = MapLazyAssets.Get().GoddessPositions.Values
            .OrderBy(g => int.TryParse(g.Id, out var id) ? id : int.MaxValue)
            .GroupBy(g => g.Country)
            .Select(grp => grp.Key);
        CountryList.Clear();
        foreach (var country in countries)
        {
            if (!string.IsNullOrEmpty(country))
            {
                CountryList.Add(country);
            }
        }

        _selectedCountry = _tpConfig.ReviveStatueOfTheSevenCountry;
        UpdateAreas(SelectedCountry);
        _selectedArea = _tpConfig.ReviveStatueOfTheSevenArea;
        UpdateRevivePoint(SelectedCountry, SelectedArea);
    }

    private void UpdateAreas(string country)
    {
        Areas.Clear();
        SelectedArea = string.Empty;
        if (string.IsNullOrEmpty(country)) return;

        var areas = MapLazyAssets.Get().GoddessPositions.Values
            .Where(g => g.Country == country)
            .OrderBy(g => int.TryParse(g.Id, out var id) ? id : int.MaxValue)
            .GroupBy(g => g.Level1Area)
            .Select(grp => grp.Key);
        foreach (var area in areas)
        {
            if (!string.IsNullOrEmpty(area))
            {
                Areas.Add(area);
            }
        }
    }

    // 当国家或区域改变时更新坐标
    private void UpdateRevivePoint(string country, string area)
    {
        if (string.IsNullOrEmpty(country) || string.IsNullOrEmpty(area)) return;

        var goddess = MapLazyAssets.Get().GoddessPositions.Values
            .FirstOrDefault(g => g.Country == country && g.Level1Area == area);
        if (goddess == null) return;
        _tpConfig.ReviveStatueOfTheSevenCountry = country;
        _tpConfig.ReviveStatueOfTheSevenArea = area;
        _tpConfig.ReviveStatueOfTheSevenPointX = goddess.X;
        _tpConfig.ReviveStatueOfTheSevenPointY = goddess.Y;
        _tpConfig.ReviveStatueOfTheSeven = goddess;
    }

    [RelayCommand]
    public void OnRefreshMaskSettings()
    {
        WeakReferenceMessenger.Default.Send(
            new PropertyChangedMessage<object>(this, "RefreshSettings", new object(), "重新计算控件位置"));
    }

    private void OnOverlayStyleChanged()
    {
        // 绘制层样式由 MaskWindowViewModel 监听配置变化后自动重绘，这里只需刷新布局相关设置
        OnRefreshMaskSettings();
    }

    [RelayCommand]
    private void OnResetOverlayStyle()
    {
        Config.MaskWindowConfig.ResetOverlayStyle();
        foreach (var group in OverlayStyleSettingGroups)
        {
            group.RefreshFromConfig();
        }

        OnOverlayStyleChanged();
    }

    [RelayCommand]
    private void OnOpenCustomHtmlMaskEditor()
    {
        CustomHtmlMaskEditorWindow.ShowEditor(_customHtmlMaskService, Config.MaskWindowConfig);
    }

    [RelayCommand]
    private void OnOpenCustomHtmlMaskFolder()
    {
        Directory.CreateDirectory(_customHtmlMaskService.DirectoryPath);
        Process.Start("explorer.exe", _customHtmlMaskService.DirectoryPath);
    }

    [RelayCommand]
    private void OnResetMaskOverlayLayout()
    {
        var c = Config.MaskWindowConfig;
        c.StatusListLeftRatio = 20.0 / 1920;
        c.StatusListTopRatio = 807.0 / 1080;
        c.StatusListWidthRatio = 477.0 / 1920;
        c.StatusListHeightRatio = 24.0 / 1080;

        c.LogTextBoxLeftRatio = 20.0 / 1920;
        c.LogTextBoxTopRatio = 832.0 / 1080;
        c.LogTextBoxWidthRatio = 477.0 / 1920;
        c.LogTextBoxHeightRatio = 188.0 / 1080;

        c.ResetOverlayMetricsLayout();

        OnRefreshMaskSettings();
    }

    [RelayCommand]
    private void OnSwitchMaskEnabled()
    {
        // if (Config.MaskWindowConfig.MaskEnabled)
        // {
        //     MaskWindow.Instance().Show();
        // }
        // else
        // {
        //     MaskWindow.Instance().Hide();
        // }
    }

    [RelayCommand]
    public void OnGoToHotKeyPage()
    {
        _navigationService.Navigate(typeof(HotKeyPage));
    }

    [RelayCommand]
    private async Task OpenRecognitionTemplateEditorFromImageAsync()
    {
        await _recognitionTemplateEditorService.OpenFromImageAsync();
    }

    [RelayCommand]
    public void OnSwitchTakenScreenshotEnabled()
    {
    }

    [RelayCommand]
    public void OnGoToFolder()
    {
        var path = Global.Absolute(@"log\screenshot\");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start("explorer.exe", path);
    }

    [RelayCommand]
    public void OnGoToRewardRecognitionFolder()
    {
        var path = Global.Absolute(@"log\RewardRecognition\");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start("explorer.exe", path);
    }

    [RelayCommand]
    public void OnGoToLogFolder()
    {
        var path = Global.Absolute(@"log");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start("explorer.exe", path);
    }

    [RelayCommand]
    private async Task ImportLocalScriptsRepoZip()
    {
        Directory.CreateDirectory(ScriptRepoUpdater.ReposPath);

        var dialog = new OpenFileDialog
        {
            Title = "Select Script Repository Archive",
            Filter = "Zip Files (*.zip)|*.zip",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                await ScriptRepoUpdater.Instance.ImportLocalRepoZip(dialog.FileName);
                ThemedMessageBox.Information("Offline script repository imported successfully!");
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Error($"Failed to import offline script repository: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private void OpenAboutWindow()
    {
        var aboutWindow = new AboutWindow();
        aboutWindow.Owner = Application.Current.MainWindow;
        aboutWindow.ShowDialog();
    }

    [RelayCommand]
    private void OpenKeyBindingsWindow()
    {
        var keyBindingsWindow = KeyBindingsWindow.Instance;
        keyBindingsWindow.Owner = Application.Current.MainWindow;
        keyBindingsWindow.ShowDialog();
    }


    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        await App.GetService<IUpdateService>()!.CheckUpdateAsync(new UpdateOption
        {
            Trigger = UpdateTrigger.Manual,
            Channel = UpdateChannel.Stable
        });
    }

    [RelayCommand]
    private async Task CheckUpdateAlphaAsync()
    {
        var result = await ThemedMessageBox.ShowAsync("Beta builds can be unstable!\n\nDo you want to continue checking for updates?", "Warning", MessageBoxButton.YesNo, ThemedMessageBox.MessageBoxIcon.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
        
        await App.GetService<IUpdateService>()!.CheckUpdateAsync(new UpdateOption
        {
            Trigger = UpdateTrigger.Manual,
            Channel = UpdateChannel.Alpha,
        });
    }

    // [RelayCommand]
    // private async Task GotoGithubActionAsync()
    // {
    //     await Launcher.LaunchUriAsync(
    //         new Uri("https://github.com/babalae/better-genshin-impact/actions/workflows/publish.yml"));
    // }

    [RelayCommand]
    private void OnUiLanguageSelectionChanged(object? value)
    {
        Config.OtherConfig.UiCultureInfoName = "en";
        _i18nService.ChangeLanguage("en");
        _configService.Save();
    }

    [RelayCommand]
    private async Task OnGameLangSelectionChanged(KeyValuePair<string, string> type)
    {
        await App.ServiceProvider.GetRequiredService<OcrFactory>().Unload();
    }

    [RelayCommand]
    private async Task OnPaddleOcrModelConfigChanged(PaddleOcrModelConfig value)
    {
        Config.OtherConfig.OcrConfig.PaddleOcrModelConfig = value;
        await App.ServiceProvider.GetRequiredService<OcrFactory>().Unload();
    }

    [RelayCommand]
    private void SelectCrosshairImage()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = "Select Crosshair Image"
        };
        if (dialog.ShowDialog() == true)
        {
            Config.MaskWindowConfig.CrosshairImagePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void SelectMainBackgroundImage()
    {
        var dialog = new OpenFileDialog
        {
            // 注意：不声明 *.webp —— WPF BitmapImage 原生不支持 WebP 解码，声明了会导致用户选中后加载失败
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = "Select Main Window Background Image"
        };
        if (dialog.ShowDialog() == true)
        {
            // 选图后弹出编辑器：可旋转/裁剪后保存副本使用，也可直接使用原图
            var editor = new ImageEditWindow(dialog.FileName)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            if (editor.ShowDialog() == true && !string.IsNullOrEmpty(editor.ResultImagePath))
            {
                // 主窗口 ViewModel 通过订阅配置变更事件实时刷新背景
                Config.CommonConfig.MainBackgroundImagePath = editor.ResultImagePath;
                Config.CommonConfig.MainBackgroundEnabled = true;
            }
        }
    }

    [RelayCommand]
    private void ClearMainBackgroundImage()
    {
        Config.CommonConfig.MainBackgroundImagePath = string.Empty;
        Config.CommonConfig.MainBackgroundEnabled = false;
    }
}

// 只服务于设置页：把遮罩显示开关、样式配置和辅助入口组织成折叠分组。
public sealed class OverlayStyleSettingGroup : ObservableObject
{
    private readonly MaskWindowConfig? _config;
    private readonly System.Reflection.PropertyInfo? _switchProperty;
    private readonly Action? _onSwitchChanged;

    public OverlayStyleSettingGroup(string name, string description, OverlayStyleSettingGroupKind kind, IEnumerable<OverlayStyleSettingItem> items,
        MaskWindowConfig? config = null, string? switchPropertyName = null, Action? onSwitchChanged = null)
    {
        Name = name;
        Description = description;
        Kind = kind;
        Items = new ObservableCollection<OverlayStyleSettingItem>(items);
        _config = config;
        _switchProperty = switchPropertyName == null
            ? null
            : typeof(MaskWindowConfig).GetProperty(switchPropertyName)
              ?? throw new ArgumentException($"Unknown mask switch property: {switchPropertyName}", nameof(switchPropertyName));
        _onSwitchChanged = onSwitchChanged;
    }

    public string Name { get; }

    public string Description { get; }

    public OverlayStyleSettingGroupKind Kind { get; }

    public bool IsLogGroup => Kind == OverlayStyleSettingGroupKind.Log;

    public bool IsMetricsGroup => Kind == OverlayStyleSettingGroupKind.Metrics;

    public bool IsCustomHtmlGroup => Kind == OverlayStyleSettingGroupKind.CustomHtml;

    public bool HasSwitch => _switchProperty != null;

    public bool SwitchValue
    {
        get => _config != null && _switchProperty?.GetValue(_config) is true;
        set
        {
            if (_config == null || _switchProperty == null || SwitchValue == value)
            {
                return;
            }

            _switchProperty.SetValue(_config, value);
            OnPropertyChanged();
            _onSwitchChanged?.Invoke();
        }
    }

    public ObservableCollection<OverlayStyleSettingItem> Items { get; }

    public void RefreshFromConfig()
    {
        foreach (var item in Items)
        {
            item.RefreshFromConfig();
        }

        OnPropertyChanged(nameof(SwitchValue));
    }

    public static ObservableCollection<OverlayStyleSettingGroup> Create(MaskWindowConfig config, Action onChanged)
    {
        return
        [
            new OverlayStyleSettingGroup("Global Overlay Settings", "Controls the general display behavior of the entire overlay window.", OverlayStyleSettingGroupKind.Global, CreateGlobalItems(config, onChanged)),
            new OverlayStyleSettingGroup("Log Overlay", "Displays runtime logs so you can see the current step of running tasks.", OverlayStyleSettingGroupKind.Log, [
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.LogPanelBackgroundColor), "Log Panel Background Color", "Background color of the log panel.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.LogPanelBorderColor), "Log Panel Border Color", "Border color of the log panel (hidden when thickness is 0).", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.LogPanelBorderThickness), "Log Panel Border Thickness", "Border width of the log panel (0 hides the border).", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.LogTextColor), "Log Text Color", "Text color of log entries.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.LogFontSize), "Log Font Size", "Font size of log entries.", onChanged),
                OverlayStyleSettingItem.Bool(config, nameof(MaskWindowConfig.LogShadowEnabled), "Show Log Drop Shadow", "Makes log text and panel easier to read against the game background.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.LogShadowColor), "Log Shadow Color", "Color of the log drop shadow.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.LogShadowOpacity), "Log Shadow Opacity", "Shadow intensity (0 = none, 1 = strongest).", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.LogShadowBlurRadius), "Log Shadow Blur Radius", "Shadow spread radius (higher values produce a softer shadow).", onChanged),
            ], config, nameof(MaskWindowConfig.ShowLogBox), onChanged),
            new OverlayStyleSettingGroup("Status Overlay", "Displays the active status of real-time triggers.", OverlayStyleSettingGroupKind.Status, [
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.StatusPanelBackgroundColor), "Status Bar Background Color", "Background color of the status bar.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.StatusPanelBorderColor), "Status Bar Border Color", "Border color of the status bar (hidden when thickness is 0).", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.StatusPanelBorderThickness), "Status Bar Border Thickness", "Border width of the status bar (0 hides the border).", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.StatusDisabledTextColor), "Disabled Status Text Color", "Icon and text color when a trigger is disabled.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.StatusEnabledTextColor), "Enabled Status Text Color", "Icon and text color when a trigger is enabled.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.StatusFontSize), "Status Font Size", "Font size of the status bar.", onChanged),
                OverlayStyleSettingItem.Bool(config, nameof(MaskWindowConfig.StatusShadowEnabled), "Show Status Bar Shadow", "Makes the status bar easier to read over complex backgrounds.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.StatusShadowColor), "Status Bar Shadow Color", "Color of the status bar shadow.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.StatusShadowOpacity), "Status Bar Shadow Opacity", "Shadow intensity (0 = none, 1 = strongest).", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.StatusShadowBlurRadius), "Status Bar Shadow Blur Radius", "Shadow spread radius (higher values produce a softer shadow).", onChanged),
            ], config, nameof(MaskWindowConfig.ShowStatus), onChanged),
            new OverlayStyleSettingGroup("Performance Metrics Overlay", "Displays runtime metrics such as FPS, processing time, and hardware usage.", OverlayStyleSettingGroupKind.Metrics, [
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.MetricsPanelBackgroundColor), "Metrics Bar Background Color", "Background color of the metrics bar.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.MetricsPanelBorderColor), "Metrics Bar Border Color", "Border color of the metrics bar (hidden when thickness is 0).", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.MetricsPanelBorderThickness), "Metrics Bar Border Thickness", "Border width of the metrics bar (0 hides the border).", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.MetricsTextColor), "Metrics Text Color", "Text color of metrics.", onChanged),
                OverlayStyleSettingItem.Slider(config, nameof(MaskWindowConfig.MetricsFontScale), "Metrics Bar Scale", "Additional scale factor applied to metrics font size and layout when overlay scaling is enabled (0.5x - 3.0x).", MaskWindowConfig.MinLogFontScale, MaskWindowConfig.MaxLogFontScale, 0.1, onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.MetricsFontSize), "Metrics Font Size", "Font size of the metrics bar.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.MetricsLineHeight), "Metrics Row Height", "Height of each metrics row.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.MetricsItemWidth), "Single Metric Item Width", "Width occupied by each metric item.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.MetricsNameColumnWidth), "Metric Name Column Width", "Width of the metric label column.", onChanged),
                OverlayStyleSettingItem.Bool(config, nameof(MaskWindowConfig.MetricsShadowEnabled), "Show Metrics Bar Shadow", "Makes the metrics bar easier to read over complex backgrounds.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.MetricsShadowColor), "Metrics Bar Shadow Color", "Color of the metrics bar shadow.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.MetricsShadowOpacity), "Metrics Bar Shadow Opacity", "Shadow intensity (0 = none, 1 = strongest).", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.MetricsShadowBlurRadius), "Metrics Bar Shadow Blur Radius", "Shadow spread radius (higher values produce a softer shadow).", onChanged),
            ], config, nameof(MaskWindowConfig.ShowOverlayMetrics), onChanged),
            new OverlayStyleSettingGroup("Cardinal Directions Overlay", "Displays N, S, E, W labels around the minimap to assist with orientation.", OverlayStyleSettingGroupKind.Direction, [
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.DirectionTextColor), "Direction Text Color", "Color of the cardinal direction labels around the minimap.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.DirectionFontSize), "Direction Font Size", "Font size of the minimap direction labels.", onChanged),
                OverlayStyleSettingItem.Bool(config, nameof(MaskWindowConfig.DirectionShadowEnabled), "Show Direction Text Shadow", "Makes direction labels easier to read over complex backgrounds.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.DirectionShadowColor), "Direction Text Shadow Color", "Color of the direction text shadow.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.DirectionShadowOpacity), "Direction Text Shadow Opacity", "Shadow intensity (0 = none, 1 = strongest).", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.DirectionShadowBlurRadius), "Direction Text Shadow Blur Radius", "Shadow spread radius (higher values produce a softer shadow).", onChanged),
            ], config, nameof(MaskWindowConfig.DirectionsEnabled), onChanged),
            new OverlayStyleSettingGroup("Recognition Results Overlay", "Displays bounding boxes and labels from image recognition.", OverlayStyleSettingGroupKind.Recognition, [
                OverlayStyleSettingItem.Bool(config, nameof(MaskWindowConfig.RecognitionUseDrawableStyle), "Unify Recognition Box Colors", "When disabled, uses task-defined colors; when enabled, uses the unified colors below.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.RecognitionRectStrokeColor), "Recognition Box Border Color", "Color used for recognition bounding boxes when unified color is enabled.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.RecognitionRectStrokeThickness), "Recognition Box Line Width", "Line thickness of recognition bounding boxes when unified color is enabled.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.RecognitionLineStrokeColor), "Recognition Line Color", "Color used for recognition lines when unified color is enabled.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.RecognitionLineStrokeThickness), "Recognition Line Width", "Line thickness of recognition lines when unified color is enabled.", onChanged),
                OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.RecognitionTextColor), "Recognition Text Color", "Color of standard recognition result labels.", onChanged),
                OverlayStyleSettingItem.Number(config, nameof(MaskWindowConfig.RecognitionTextFontSize), "Recognition Font Size", "Base font size of standard recognition result labels.", onChanged),
            ], config, nameof(MaskWindowConfig.DisplayRecognitionResultsOnMask), onChanged),
            new OverlayStyleSettingGroup("Custom HTML Overlay", "Create custom overlays using HTML.", OverlayStyleSettingGroupKind.CustomHtml, [
                OverlayStyleSettingItem.Bool(config, nameof(MaskWindowConfig.CustomHtmlMaskClickThrough), "HTML Overlay Click-Through", "When enabled, mouse clicks pass through the overlay; when disabled, HTML content can receive clicks and input.", onChanged),
                OverlayStyleSettingItem.Bool(config, nameof(MaskWindowConfig.CustomHtmlMaskAutoReloadOnSave), "Auto-Reload HTML on Save", "Automatically refreshes open custom HTML overlays when saved.", onChanged),
            ], config, nameof(MaskWindowConfig.CustomHtmlMaskEnabled), onChanged),
        ];
    }

    private static IEnumerable<OverlayStyleSettingItem> CreateGlobalItems(MaskWindowConfig config, Action onChanged)
    {
        yield return OverlayStyleSettingItem.Bool(config, nameof(MaskWindowConfig.OverlayScalingEnabled), "Enable Overlay Font Scaling", "Scales log, status, FPS, and metrics font sizes based on game resolution and system DPI; uses base sizes when disabled.", onChanged);
        yield return OverlayStyleSettingItem.Slider(config, nameof(MaskWindowConfig.LogFontScale), "Overlay Font Scale Factor", "Additional scale factor applied to log, status, and FPS base font sizes when scaling is enabled (0.5x - 3.0x).", MaskWindowConfig.MinLogFontScale, MaskWindowConfig.MaxLogFontScale, 0.1, onChanged);
        yield return OverlayStyleSettingItem.Slider(config, nameof(MaskWindowConfig.TextOpacity), "Overlay Text Opacity", "Adjusts opacity of all text on the overlay (1 = opaque, 0 = transparent).", 0, 1, 0.1, onChanged);
        yield return OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.OverlayWindowBackgroundColor), "Main Overlay Background Color", "Background color of the overlay window itself.", onChanged);

        if (WinePlatformAddon.IsRunningOnWine)
        {
            yield return OverlayStyleSettingItem.Color(config, nameof(MaskWindowConfig.WineOverlayBackgroundColor), "Wine Compatibility Background Color", "Compatibility background color used only under Wine.", onChanged);
        }
    }
}

public sealed class OverlayStyleSettingItem : ObservableObject
{
    private static readonly IReadOnlyList<OverlayStyleColorOption> BaseColorOptions =
    [
        new("Transparent", MaskWindowConfig.DefaultTransparentColor),
        new("Nearly Transparent Black", MaskWindowConfig.DefaultOverlayWindowBackgroundColor),
        new("Semi-Transparent Black", MaskWindowConfig.DefaultPanelBorderColor),
        new("Light Gray", "LightGray"),
        new("White", "White"),
        new("Black", "Black"),
        new("Pure Black", MaskWindowConfig.DefaultShadowColor),
        new("Light Green", "LightGreen"),
        new("Green", "Green"),
        new("Red", "Red"),
        new("Orange", "Orange"),
        new("Yellow", "Yellow"),
        new("Deep Sky Blue", "DeepSkyBlue"),
        new("Dodger Blue", "DodgerBlue"),
        new("Purple", "Purple"),
        new("Hot Pink", "HotPink")
    ];

    private static readonly OverlayStyleColorOption WineColorOption =
        new("Wine Semi-Transparent Black", MaskWindowConfig.DefaultWineOverlayBackgroundColor);

    private readonly MaskWindowConfig _config;
    private readonly System.Reflection.PropertyInfo _property;
    private readonly Action _onChanged;

    private OverlayStyleSettingItem(MaskWindowConfig config, string propertyName, string displayName, string description, OverlayStyleSettingKind kind, Action onChanged)
    {
        _config = config;
        _property = typeof(MaskWindowConfig).GetProperty(propertyName)
                    ?? throw new ArgumentException($"Unknown mask style property: {propertyName}", nameof(propertyName));
        _onChanged = onChanged;
        DisplayName = displayName;
        Description = description;
        Kind = kind;
    }

    public string DisplayName { get; }

    public string Description { get; }

    public OverlayStyleSettingKind Kind { get; }

    public bool IsText => Kind == OverlayStyleSettingKind.Text;

    public bool IsColor => Kind == OverlayStyleSettingKind.Color;

    public bool IsNumber => Kind == OverlayStyleSettingKind.Number;

    public bool IsSlider => Kind == OverlayStyleSettingKind.Slider;

    public bool IsBool => Kind == OverlayStyleSettingKind.Bool;

    public double SliderMinimum { get; private init; }

    public double SliderMaximum { get; private init; }

    public double SliderTickFrequency { get; private init; }

    public string TextValue
    {
        get => FormatValue(_property.GetValue(_config));
        set
        {
            if (Kind == OverlayStyleSettingKind.Bool || Kind == OverlayStyleSettingKind.Color || Kind == OverlayStyleSettingKind.Slider || !TrySetValue(value))
            {
                return;
            }

            OnPropertyChanged();
            _onChanged();
        }
    }

    public IReadOnlyList<OverlayStyleColorOption> ColorOptions
    {
        get
        {
            var commonColorOptions = GetCommonColorOptions();
            var currentValue = FormatValue(_property.GetValue(_config));
            if (string.IsNullOrWhiteSpace(currentValue)
                || commonColorOptions.Any(option => string.Equals(option.Value, currentValue, StringComparison.OrdinalIgnoreCase)))
            {
                return commonColorOptions;
            }

            return [.. commonColorOptions, new OverlayStyleColorOption($"Custom Color ({currentValue})", currentValue)];
        }
    }

    public string ColorValue
    {
        get => FormatValue(_property.GetValue(_config));
        set
        {
            if (Kind != OverlayStyleSettingKind.Color
                || string.IsNullOrWhiteSpace(value)
                || string.Equals(ColorValue, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _property.SetValue(_config, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColorOptions));
            _onChanged();
        }
    }

    public bool BoolValue
    {
        get => _property.GetValue(_config) is true;
        set
        {
            if (Kind != OverlayStyleSettingKind.Bool || BoolValue == value)
            {
                return;
            }

            _property.SetValue(_config, value);
            OnPropertyChanged();
            _onChanged();
        }
    }

    public double SliderValue
    {
        get => _property.GetValue(_config) is double value ? value : 0;
        set
        {
            if (Kind != OverlayStyleSettingKind.Slider || Math.Abs(SliderValue - value) < 0.0001)
            {
                return;
            }

            _property.SetValue(_config, value);
            OnPropertyChanged();
            _onChanged();
        }
    }

    public static OverlayStyleSettingItem Text(MaskWindowConfig config, string propertyName, string displayName, string description, Action onChanged)
    {
        return new OverlayStyleSettingItem(config, propertyName, displayName, description, OverlayStyleSettingKind.Text, onChanged);
    }

    public static OverlayStyleSettingItem Color(MaskWindowConfig config, string propertyName, string displayName, string description, Action onChanged)
    {
        return new OverlayStyleSettingItem(config, propertyName, displayName, description, OverlayStyleSettingKind.Color, onChanged);
    }

    public static OverlayStyleSettingItem Number(MaskWindowConfig config, string propertyName, string displayName, string description, Action onChanged)
    {
        return new OverlayStyleSettingItem(config, propertyName, displayName, description, OverlayStyleSettingKind.Number, onChanged);
    }

    public static OverlayStyleSettingItem Slider(MaskWindowConfig config, string propertyName, string displayName, string description, double minimum, double maximum, double tickFrequency, Action onChanged)
    {
        return new OverlayStyleSettingItem(config, propertyName, displayName, description, OverlayStyleSettingKind.Slider, onChanged)
        {
            SliderMinimum = minimum,
            SliderMaximum = maximum,
            SliderTickFrequency = tickFrequency
        };
    }

    public static OverlayStyleSettingItem Bool(MaskWindowConfig config, string propertyName, string displayName, string description, Action onChanged)
    {
        return new OverlayStyleSettingItem(config, propertyName, displayName, description, OverlayStyleSettingKind.Bool, onChanged);
    }

    public void RefreshFromConfig()
    {
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(ColorOptions));
        OnPropertyChanged(nameof(ColorValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(SliderValue));
    }

    private bool TrySetValue(string value)
    {
        if (_property.PropertyType == typeof(string))
        {
            _property.SetValue(_config, value ?? string.Empty);
            return true;
        }

        if (_property.PropertyType == typeof(double)
            && (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariantValue)
                || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out invariantValue)))
        {
            _property.SetValue(_config, invariantValue);
            return true;
        }

        return false;
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            string text => text,
            null => string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static IReadOnlyList<OverlayStyleColorOption> GetCommonColorOptions()
    {
        return WinePlatformAddon.IsRunningOnWine ? [.. BaseColorOptions, WineColorOption] : BaseColorOptions;
    }
}

public sealed class OverlayStyleColorOption
{
    private static readonly BrushConverter BrushConverter = new();

    public OverlayStyleColorOption(string displayName, string value)
    {
        DisplayName = displayName;
        Value = value;
        SwatchBrush = CreateSwatchBrush(value);
    }

    public string DisplayName { get; }

    public string Value { get; }

    public Brush SwatchBrush { get; }

    private static Brush CreateSwatchBrush(string value)
    {
        try
        {
            if (BrushConverter.ConvertFromString(value) is Brush brush)
            {
                brush.Freeze();
                return brush;
            }
        }
        catch
        {
            // 历史配置可能存在手动填写的无效颜色，色块失败时不影响下拉项显示。
        }

        return Brushes.Transparent;
    }
}

public enum OverlayStyleSettingGroupKind
{
    Global,
    Log,
    Status,
    Metrics,
    Direction,
    Recognition,
    CustomHtml
}

public enum OverlayStyleSettingKind
{
    Text,
    Color,
    Number,
    Slider,
    Bool
}

// 只服务于设置页：把固定指标枚举、显示文案和配置字典中的开关包装成复选框可双向绑定的对象。
public sealed class OverlayMetricSettingItem : ObservableObject
{
    private readonly MaskWindowConfig _config;
    private readonly Action _onChanged;
    private bool _isEnabled;

    public OverlayMetricSettingItem(MaskWindowConfig config, OverlayMetricItem item, Action onChanged)
    {
        _config = config;
        _onChanged = onChanged;
        Item = item;
        DisplayName = OverlayMetricItemDefaults.GetDisplayName(item);
        ToolTipText = OverlayMetricItemDefaults.GetToolTipText(item);
        _isEnabled = config.IsOverlayMetricEnabled(item);
    }

    public OverlayMetricItem Item { get; }

    public string DisplayName { get; }

    public string ToolTipText { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            _config.SetOverlayMetricEnabled(Item, value);
            _onChanged();
        }
    }
}
