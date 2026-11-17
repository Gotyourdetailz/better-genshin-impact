using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.ChildSession;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace BetterGenshinImpact.ViewModel.Windows;

public partial class ChildSessionWindowViewModel : ViewModel
{
    private const string DesktopHelpUrl = "https://www.bettergi.com/feats/command/session.html";
    private const double DefaultWindowWidth = 1280d;
    private const double SmallWindowWidth = 500d;

    private readonly ChildSessionService _childSessionService;
    private readonly DispatcherTimer _notificationTimer;
    private bool _startRequested;
    private ChildSessionDiagnosticsWindow? _diagnosticsWindow;

    [ObservableProperty]
    private bool _isDiagnosticsSuggested;

    [ObservableProperty]
    private Brush _connectionStatusBrush = Brushes.Red;

    [ObservableProperty]
    private string _connectionStatusToolTip = "Virtual Desktop has not started";

    [ObservableProperty]
    private string _connectionStatusTitle = "Virtual Desktop Not Started";

    [ObservableProperty]
    private string _connectionStatusDescription = "Click \"Start & Connect\" to create an isolated desktop and connect over RDP.";

    [ObservableProperty]
    private string _rdpStatusText = "RDP Disconnected";

    [ObservableProperty]
    private string _childSessionStatusText = "Virtual Desktop session not created";

    [ObservableProperty]
    private bool _isRdpConnected;

    [ObservableProperty]
    private bool _isConnectionPromptVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionInteractionEnabled))]
    private bool _isSessionClosing;

    [ObservableProperty]
    private bool _isTopmost;

    [ObservableProperty]
    private bool _isAdaptive = true;

    [ObservableProperty]
    private bool _isOneToOne;

    [ObservableProperty]
    private bool _keepAspectRatio = true;

    [ObservableProperty]
    private WindowPositionConfig? _normalWindowPosition;

    [ObservableProperty]
    private WindowPositionConfig? _smallWindowPosition;

    [ObservableProperty]
    private int _smallWindowResizeRequest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowSizeModeMenuHeader))]
    [NotifyPropertyChangedFor(nameof(WindowSizeModeMenuToolTip))]
    [NotifyPropertyChangedFor(nameof(WindowResizeTargetWidth))]
    private bool _isSmallWindowMode;

    [ObservableProperty]
    private bool _sendSystemShortcutsToRemote = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameMouseModeButtonText))]
    [NotifyPropertyChangedFor(nameof(GameMouseModeButtonToolTip))]
    private bool _isGameMouseModeEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchModeMenuHeader))]
    [NotifyPropertyChangedFor(nameof(WatchModeMenuToolTip))]
    private bool _isWatchModeEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioButtonToolTip))]
    private bool _isAudioMuted;

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private string _notificationTitle = "";

    [ObservableProperty]
    private string _notificationMessage = "";

    [ObservableProperty]
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleSendSystemShortcutsToRemoteCommand))]
    private bool _isSystemShortcutsReconnectPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleAudioMutedCommand))]
    private bool _isAudioReconnectPending;

    public bool IsDefaultResolutionSelected => true;

    public string TopmostButtonToolTip => IsTopmost ? "Unpin" : "Always on Top";

    public string GameMouseModeButtonText =>
        IsGameMouseModeEnabled ? "Game Mouse" : "Standard Mouse";

    public string GameMouseModeButtonToolTip => IsGameMouseModeEnabled
        ? "Active when BetterGI is open inside Virtual Desktop. Locks mouse inside this window while focused; hold Alt to release temporarily."
        : "Switch to Game Mouse mode. Locks mouse inside this window while focused when BetterGI is running in Virtual Desktop; hold Alt to release temporarily.";

    public string AudioButtonToolTip => IsAudioMuted
        ? "Unmute Virtual Desktop audio without affecting the main desktop; automatically reconnects RDP when toggled"
        : "Mute Virtual Desktop audio without affecting the main desktop; automatically reconnects RDP when toggled";

    public string WindowSizeModeMenuHeader => IsSmallWindowMode ? "Restore Size" : "Compact Mode";

    public string WindowSizeModeMenuToolTip => IsSmallWindowMode
        ? "Restore Virtual Desktop window to its default size"
        : "Scale Virtual Desktop window to 750px wide while maintaining 16:9 aspect ratio";

    public string WatchModeMenuHeader => IsWatchModeEnabled ? "Control Mode" : "View-Only Mode";

    public string WatchModeMenuToolTip => IsWatchModeEnabled
        ? "Exit View-Only mode"
        : "View Virtual Desktop screen only; mouse and keyboard input will be ignored";

    public double WindowResizeTargetWidth => IsSmallWindowMode
        ? SmallWindowWidth
        : DefaultWindowWidth;

    public bool HasChildSession => _childSessionService.ChildSessionId is not null;

    public bool IsSessionInteractionEnabled => !IsSessionClosing;

    public ChildSessionWindowViewModel(ChildSessionService childSessionService)
    {
        _childSessionService = childSessionService;
        _isTopmost = _childSessionService.TopmostEnabled;
        _isAdaptive = _childSessionService.SmartSizingEnabled;
        _isOneToOne = !_isAdaptive;
        _keepAspectRatio = _childSessionService.KeepAspectRatio;
        _normalWindowPosition = _childSessionService.NormalWindowPosition;
        _smallWindowPosition = _childSessionService.SmallWindowPosition;
        _sendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
        _isGameMouseModeEnabled = _childSessionService.IsGameMouseModeEnabled;
        _isAudioMuted = _childSessionService.AudioMuted;
        _notificationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _notificationTimer.Tick += OnNotificationTimerTick;
        _childSessionService.StateChanged += OnChildSessionStateChanged;
        _childSessionService.ConnectionFailed += OnChildSessionConnectionFailed;
        _childSessionService.SystemShortcutsReconnectCompleted +=
            OnSystemShortcutsReconnectCompleted;
        _childSessionService.AudioReconnectCompleted += OnAudioReconnectCompleted;
        UpdateConnectionStatus();
    }

    partial void OnNormalWindowPositionChanged(WindowPositionConfig? value)
    {
        _childSessionService.NormalWindowPosition = value;
    }

    partial void OnSmallWindowPositionChanged(WindowPositionConfig? value)
    {
        _childSessionService.SmallWindowPosition = value;
    }

    public async Task LogoffAndHideAsync()
    {
        if (IsSessionClosing)
        {
            return;
        }

        IsSessionClosing = true;
        UpdateConnectionStatus();

        try
        {
            // 先让“正在关闭”状态完成渲染，再执行可能耗时的注销操作。
            await Dispatcher.Yield(DispatcherPriority.Render);
            await _childSessionService.LogoffAndHideAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsSessionClosing = false;
            UpdateConnectionStatus();
        }
    }

    partial void OnIsTopmostChanged(bool value)
    {
        OnPropertyChanged(nameof(TopmostButtonToolTip));
    }

    /// <summary>
    /// 启动桌面分身的命令入口。直接尝试连接，失败后提供诊断按钮，
    /// 由用户主动打开诊断面板检查环境。
    /// </summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        // 环境条件只在用户主动诊断时检查，避免挡住本来可以连上的用户。
        IsDiagnosticsSuggested = false;
        _startRequested = true;
        IsConnectionPromptVisible = false;

        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await ExecuteAsync(_childSessionService.StartAsync, suggestDiagnosticsOnError: true);
        }
        finally
        {
            _startRequested = false;
            UpdateConnectionStatus();
        }
    }

    [RelayCommand]
    private void Hide()
    {
        Execute(_childSessionService.HideWindow);
    }

    [RelayCommand]
    private void SwitchWindow()
    {
        Execute(_childSessionService.ShowChildSessionTaskView);
    }

    [RelayCommand]
    private Task LaunchBetterGiAsync()
    {
        return ExecuteAsync(_childSessionService.LaunchBetterGiAsync);
    }

    [RelayCommand]
    private void SelectDefaultResolution()
    {
        OnPropertyChanged(nameof(IsDefaultResolutionSelected));
    }

    [RelayCommand]
    private void UseAdaptive()
    {
        if (!Execute(() => _childSessionService.SetSmartSizing(true)))
        {
            return;
        }

        IsAdaptive = true;
        IsOneToOne = false;
        OnPropertyChanged(nameof(IsAdaptive));
        OnPropertyChanged(nameof(IsOneToOne));
    }

    [RelayCommand]
    private void UseOneToOne()
    {
        if (!Execute(() => _childSessionService.SetSmartSizing(false)))
        {
            return;
        }

        IsAdaptive = false;
        IsOneToOne = true;
        OnPropertyChanged(nameof(IsAdaptive));
        OnPropertyChanged(nameof(IsOneToOne));
    }

    [RelayCommand]
    private void ToggleSmallWindowMode()
    {
        IsSmallWindowMode = !IsSmallWindowMode;
        SmallWindowResizeRequest++;
    }

    [RelayCommand]
    private void ToggleKeepAspectRatio()
    {
        if (!Execute(() => _childSessionService.SetKeepAspectRatio(!KeepAspectRatio)))
        {
            return;
        }

        KeepAspectRatio = _childSessionService.KeepAspectRatio;
    }

    [RelayCommand(CanExecute = nameof(CanToggleSendSystemShortcutsToRemote))]
    private void ToggleSendSystemShortcutsToRemote()
    {
        try
        {
            var reconnectStarted = _childSessionService.SetSendSystemShortcutsToRemote(
                !SendSystemShortcutsToRemote);
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
            IsSystemShortcutsReconnectPending = reconnectStarted;
            UpdateConnectionStatus();

            ShowNotification(
                reconnectStarted ? "Applying Settings" : "Settings Saved",
                reconnectStarted
                    ? "RDP is automatically reconnecting; system shortcut settings will take effect once connected."
                    : "No active Virtual Desktop session; system shortcut settings will apply on next connection.",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsSystemShortcutsReconnectPending = false;
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
            ShowNotification(
                "Failed to Switch Settings",
                exception.GetBaseException().Message,
                InfoBarSeverity.Error,
                TimeSpan.FromSeconds(8));
            UpdateConnectionStatus();
        }
    }

    private bool CanToggleSendSystemShortcutsToRemote()
    {
        return !IsSystemShortcutsReconnectPending;
    }

    [RelayCommand]
    private void ToggleGameMouseMode()
    {
        var enabled = !IsGameMouseModeEnabled;
        if (!Execute(() => _childSessionService.SetGameMouseModeEnabled(enabled)))
        {
            IsGameMouseModeEnabled = _childSessionService.IsGameMouseModeEnabled;
            return;
        }

        IsGameMouseModeEnabled = _childSessionService.IsGameMouseModeEnabled;
        ShowNotification(
            enabled ? "Game Mouse Enabled" : "Standard Mouse Enabled",
            enabled
                ? "Active when BetterGI is open inside Virtual Desktop. Locks mouse inside this window while focused; hold Alt to release temporarily."
                : "BetterGI stopped forwarding relative mouse input to Virtual Desktop.",
            enabled ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
    }

    [RelayCommand]
    private void ToggleWatchMode()
    {
        var enabled = !IsWatchModeEnabled;
        if (!Execute(() => _childSessionService.SetWatchModeEnabled(enabled)))
        {
            IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
            return;
        }

        IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
        ShowNotification(
            enabled ? "View-Only Mode Enabled" : "View-Only Mode Disabled",
            enabled
                ? "Local mouse and keyboard input will not affect Virtual Desktop, while toolbar shortcuts remain active."
                : "Local mouse and keyboard input restored for Virtual Desktop.",
            enabled ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
    }

    [RelayCommand(CanExecute = nameof(CanToggleAudioMuted))]
    private void ToggleAudioMuted()
    {
        try
        {
            var reconnectStarted = _childSessionService.SetAudioMuted(!IsAudioMuted);
            IsAudioMuted = _childSessionService.AudioMuted;
            IsAudioReconnectPending = reconnectStarted;
            UpdateConnectionStatus();

            ShowNotification(
                reconnectStarted ? "Applying Audio Settings" : "Audio Settings Saved",
                reconnectStarted
                    ? "RDP is automatically reconnecting; audio settings will take effect once connected."
                    : IsAudioMuted
                        ? "Virtual Desktop will remain muted on next connection."
                        : "Virtual Desktop will play audio on next connection.",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            IsAudioReconnectPending = false;
            IsAudioMuted = _childSessionService.AudioMuted;
            ShowNotification(
                "Failed to Switch Audio Settings",
                exception.GetBaseException().Message,
                InfoBarSeverity.Error,
                TimeSpan.FromSeconds(8));
            UpdateConnectionStatus();
        }
    }

    private bool CanToggleAudioMuted()
    {
        return !IsAudioReconnectPending;
    }

    [RelayCommand]
    private void ShowDesktop()
    {
        Execute(_childSessionService.ShowChildSessionDesktop);
    }

    [RelayCommand]
    private void ShowTaskView()
    {
        Execute(_childSessionService.ShowChildSessionTaskView);
    }

    [RelayCommand]
    private async Task LaunchExecutableAsync()
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "Select an application to launch as administrator in BetterGI Virtual Desktop",
            Filter = "Windows Applications (*.exe)|*.exe",
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true,
            Multiselect = false
        };

        if (fileDialog.ShowDialog() != true)
        {
            return;
        }

        var executablePath = Path.GetFullPath(fileDialog.FileName);
        await ExecuteAsync(() => _childSessionService.LaunchExecutableAsync(executablePath));
    }

    [RelayCommand]
    private void ToggleTopmost()
    {
        if (!Execute(() => _childSessionService.SetTopmost(!IsTopmost)))
        {
            return;
        }

        IsTopmost = _childSessionService.TopmostEnabled;
    }

    [RelayCommand]
    private static void OpenDesktopHelp()
    {
        Process.Start(new ProcessStartInfo(DesktopHelpUrl)
        {
            UseShellExecute = true
        });
    }

    private async Task ExecuteAsync(Func<Task> action, bool suggestDiagnosticsOnError = false)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            // 启动直接抛异常（例如系统缺少 Child Session API）时也要提供诊断入口：
            // 用户点击后再检查环境，帮助定位具体缺少的条件。
            if (suggestDiagnosticsOnError)
            {
                SuggestDiagnosticsOnFailure();
            }

            ShowError(exception);
        }
        finally
        {
            UpdateConnectionStatus();
        }
    }

    private bool Execute(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
        finally
        {
            UpdateConnectionStatus();
        }
    }

    private void OnChildSessionStateChanged(object? sender, EventArgs e)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            UpdateConnectionStatus();
            return;
        }

        _ = Application.Current.Dispatcher.BeginInvoke(UpdateConnectionStatus);
    }

    /// <summary>
    /// 打开诊断与修复面板。已经打开时把它叫到前面，不重复创建。
    /// </summary>
    [RelayCommand]
    private void OpenDiagnostics()
    {
        if (_diagnosticsWindow is { IsLoaded: true } existing)
        {
            // 最小化时 Activate() 不会把窗口还原，用户点菜单会以为没反应。
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = App.GetService<ChildSessionDiagnosticsWindow>();
        if (window is null)
        {
            return;
        }

        // 面板属于桌面分身窗口，挂上去后跟随它一起关闭。
        window.Owner = Application.Current.Windows.OfType<ChildSessionWindow>().FirstOrDefault()
                       ?? Application.Current.MainWindow;
        window.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow = window;
        window.Show();
    }

    /// <summary>
    /// 启动失败时显示诊断入口，由用户点击后打开面板。
    ///
    /// 失败会被重试流程触发多次，这里只更新按钮的可见状态，
    /// 用户也可以通过菜单打开，不会反复抢焦点。
    /// </summary>
    private void SuggestDiagnosticsOnFailure()
    {
        IsDiagnosticsSuggested = true;
    }

    private void OnChildSessionConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsSystemShortcutsReconnectPending = false;
                IsAudioReconnectPending = false;
                UpdateConnectionStatus();
                ShowNotification(
                    "RDP Connection Failed",
                    e.Message,
                    InfoBarSeverity.Error,
                    TimeSpan.FromSeconds(10));

                // 失败原因往往在环境里（服务被禁用、被补丁接管等），
                // 提供诊断按钮，让用户主动检查，无需自己翻日志。
                SuggestDiagnosticsOnFailure();
            }));
    }

    private void OnSystemShortcutsReconnectCompleted(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsSystemShortcutsReconnectPending = false;
                UpdateConnectionStatus();
                ShowNotification(
                    "Settings Applied",
                    SendSystemShortcutsToRemote
                        ? "System shortcuts are now sent to Virtual Desktop."
                        : "System shortcuts now apply to the local PC.",
                    InfoBarSeverity.Success);
            }));
    }

    private void OnAudioReconnectCompleted(object? sender, EventArgs e)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                IsAudioReconnectPending = false;
                IsAudioMuted = _childSessionService.AudioMuted;
                UpdateConnectionStatus();
                ShowNotification(
                    IsAudioMuted ? "Virtual Desktop Muted" : "Virtual Desktop Audio Enabled",
                    IsAudioMuted
                        ? "Audio is muted for Virtual Desktop only; main desktop apps remain unaffected."
                        : "Virtual Desktop audio has been restored.",
                    InfoBarSeverity.Success);
            }));
    }

    private void ShowNotification(
        string title,
        string message,
        InfoBarSeverity severity,
        TimeSpan? duration = null)
    {
        _notificationTimer.Stop();
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationSeverity = severity;
        IsNotificationOpen = true;
        _notificationTimer.Interval = duration ?? TimeSpan.FromSeconds(5);
        _notificationTimer.Start();
    }

    private void OnNotificationTimerTick(object? sender, EventArgs e)
    {
        _notificationTimer.Stop();
        IsNotificationOpen = false;
    }

    private void UpdateConnectionStatus()
    {
        var connectedState = _childSessionService.ConnectedState;
        var childSessionId = _childSessionService.ChildSessionId;
        IsTopmost = _childSessionService.TopmostEnabled;
        IsAdaptive = _childSessionService.SmartSizingEnabled;
        IsOneToOne = !IsAdaptive;
        KeepAspectRatio = _childSessionService.KeepAspectRatio;
        SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
        IsGameMouseModeEnabled = _childSessionService.IsGameMouseModeEnabled;
        IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
        IsAudioMuted = _childSessionService.AudioMuted;
        IsRdpConnected = connectedState == 1;
        IsConnectionPromptVisible = IsSessionClosing || connectedState == 0 && !_startRequested;

        if (IsSessionClosing)
        {
            ConnectionStatusBrush = Brushes.Orange;
            ConnectionStatusTitle = "Closing Virtual Desktop";
            ConnectionStatusDescription = "Disconnecting RDP and signing out of the Virtual Desktop session; this may take a moment.";
            RdpStatusText = connectedState == 0 ? "RDP Disconnected" : "Disconnecting RDP";
            ChildSessionStatusText = childSessionId is null
                ? "Verifying Virtual Desktop session sign-out"
                : $"Signing out Virtual Desktop session {childSessionId.Value}";
            ConnectionStatusToolTip = "Operations are paused while closing; this window will hide automatically when complete.";
            OnPropertyChanged(nameof(HasChildSession));
            return;
        }

        if (IsRdpConnected)
        {
            IsDiagnosticsSuggested = false;
            ConnectionStatusBrush = Brushes.LimeGreen;
            ConnectionStatusTitle = "Virtual Desktop Connected";
            ConnectionStatusDescription = "RDP connection is healthy; you can interact directly inside Virtual Desktop.";
        }
        else if (connectedState == 2)
        {
            ConnectionStatusBrush = childSessionId is null ? Brushes.Red : Brushes.DodgerBlue;
            ConnectionStatusTitle = "Connecting to Virtual Desktop";
            ConnectionStatusDescription = "Creating or restoring RDP connection; please wait.";
        }
        else if (childSessionId is not null)
        {
            ConnectionStatusBrush = Brushes.DodgerBlue;
            ConnectionStatusTitle = "Virtual Desktop Started, Waiting to Connect";
            ConnectionStatusDescription = "Virtual Desktop session is still running; click \"Start & Connect\" to re-establish the RDP connection.";
        }
        else
        {
            ConnectionStatusBrush = Brushes.Red;
            ConnectionStatusTitle = "Virtual Desktop Not Started";
            ConnectionStatusDescription = "Click \"Start & Connect\" to create an isolated desktop and connect over RDP.";
        }

        RdpStatusText = connectedState switch
        {
            0 => "RDP Disconnected",
            1 => "RDP Connected",
            2 => "RDP Connecting",
            _ => $"Unknown RDP state ({connectedState})"
        };
        ChildSessionStatusText = childSessionId is null
            ? "Virtual Desktop session not created"
            : $"Virtual Desktop session {childSessionId.Value}";
        ConnectionStatusToolTip = _childSessionService.StatusText;
        OnPropertyChanged(nameof(HasChildSession));
    }

    private static void ShowError(Exception exception)
    {
        var actualException = exception.GetBaseException();
        var suggestion = actualException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
            ? "\n\nAccess denied by Windows. Please verify BetterGI is running as administrator."
            : string.Empty;

        ThemedMessageBox.Error(
            $"{actualException.Message}{suggestion}",
            "BetterGI Virtual Desktop");
    }
}
