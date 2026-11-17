using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.View.Windows;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace BetterGenshinImpact.Service.ChildSession;

public sealed class ChildSessionService : IDisposable
{
    private static readonly DrawingSize DefaultDesktopSize = new(1920, 1080);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);
    private const int InitialConnectionRetryCount = 3;
    private const int RdpSettingsReconnectRetryCount = 2;
    private const int ErrorTimeout = 1460;

    private readonly IServiceProvider _serviceProvider;
    private readonly InstanceService _instanceService;
    private readonly ChildSessionConfig _config;
    private readonly ILogger<ChildSessionService> _logger;
    private readonly DispatcherTimer _statusTimer;
    private readonly SemaphoreSlim _launchSemaphore = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();

    private ChildSessionWindow? _desktopWindow;
    private bool _autoLaunchBetterGiPending;
    private TaskCompletionSource<bool>? _connectionAttemptCompletionSource;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionFailure;
    private int _initialConnectionRetriesRemaining;
    private bool _connectionRetryInProgress;
    private RdpSettingChange _pendingRdpSettingChanges;
    private int _rdpSettingsReconnectRetriesRemaining;
    private bool _rdpSettingsReconnectRetryInProgress;
    private bool _statusTickInProgress;
    private bool _disposed;
    private string? _lastOperationMessage;
    private IReadOnlyList<ChildSessionEnvironmentCheck.Issue> _environmentIssues = [];

    public event EventHandler? StateChanged;

    public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    public event EventHandler? SystemShortcutsReconnectCompleted;

    public event EventHandler? AudioReconnectCompleted;

    public string StatusText { get; private set; } = "Virtual Desktop has not started yet";

    public bool IsDesktopVisible => _desktopWindow?.IsVisible == true;

    public int ConnectedState { get; private set; }

    public uint? ChildSessionId { get; private set; }

    public bool SendSystemShortcutsToRemote { get; private set; } = true;

    public bool IsGameMouseModeEnabled => _instanceService.IsGameMouseModeEnabled;

    public bool TopmostEnabled => _config.TopmostEnabled;

    public bool SmartSizingEnabled => _config.SmartSizingEnabled;

    public bool KeepAspectRatio => _config.KeepAspectRatio;

    public bool AudioMuted => _config.AudioMuted;

    public bool WatchModeEnabled => _config.WatchModeEnabled;

    public WindowPositionConfig? NormalWindowPosition
    {
        get => _config.NormalWindowPosition;
        set => _config.NormalWindowPosition = value;
    }

    public WindowPositionConfig? SmallWindowPosition
    {
        get => _config.SmallWindowPosition;
        set => _config.SmallWindowPosition = value;
    }

    /// <summary>
    /// 系统是否已安装并启用 RDP Wrapper，供用户主动诊断时查询，不参与启动流程。
    /// </summary>
    public bool IsRdpWrapperEnabled()
    {
        // 不能只匹配 rdpwrap.dll：SuperRDP 等改版用的是别的 DLL 名。
        // 判定统一为「ServiceDll 不等于原生 termsrv.dll 就算被接管」。
        return ChildSessionEnvironmentCheck.IsServiceDllHijacked(
            ChildSessionEnvironmentCheck.ReadServiceDll());
    }

    /// <summary>
    /// 最近一次主动环境检查是否发现了可能影响桌面分身的环境设置。
    /// </summary>
    public bool HasEnvironmentIssue => _environmentIssues.Count > 0;

    /// <summary>
    /// 最近一次主动环境检查的结论文本，供界面提示与问题排查使用。
    /// </summary>
    public string EnvironmentIssueSummary
    {
        get
        {
            if (_environmentIssues.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            for (var i = 0; i < _environmentIssues.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine();
                }

                var issue = _environmentIssues[i];
                sb.Append("- ")
                  .Append(BetterGenshinImpact.Service.I18n.I18nService.Instance.Translate(issue.Title))
                  .Append(": ")
                  .Append(BetterGenshinImpact.Service.I18n.I18nService.Instance.Translate(issue.Summary));
            }

            return sb.ToString();
        }
    }

    public bool HasActiveChildSession()
    {
        if (!_instanceService.Context.IsRoot)
        {
            return false;
        }

        RefreshState();
        return ChildSessionId is not null;
    }

    public ChildSessionService(
        IServiceProvider serviceProvider,
        InstanceService instanceService,
        IConfigService configService,
        ILogger<ChildSessionService> logger)
    {
        _serviceProvider = serviceProvider;
        _instanceService = instanceService;
        _config = configService.Get().ChildSessionConfig;
        SendSystemShortcutsToRemote = _config.SendSystemShortcutsToRemote;
        _logger = logger;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
        RefreshState();
    }

    /// <summary>
    /// 启动桌面分身：确保 Child Sessions 已启用，然后建立 RDP 连接并等待登录完成。
    /// 启动过程不做环境检查，由用户主动打开诊断面板后再检查。
    /// 已经处于已连接状态时直接返回。
    /// </summary>
    public async Task StartAsync()
    {
        ThrowIfDisposed();
        EnsureChildSessionsEnabled();
        RefreshState();

        if (ConnectedState == 1)
        {
            return;
        }

        var completionSource = _connectionAttemptCompletionSource;
        if (completionSource is null || completionSource.Task.IsCompleted)
        {
            completionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionAttemptCompletionSource = completionSource;
            _lastConnectionFailure = null;

            try
            {
                ConnectCore("Starting BetterGI Virtual Desktop");
            }
            catch
            {
                _autoLaunchBetterGiPending = false;
                _initialConnectionRetriesRemaining = 0;
                completionSource.TrySetResult(false);
                _connectionAttemptCompletionSource = null;
                throw;
            }
        }

        try
        {
            await completionSource.Task.WaitAsync(
                ConnectionTimeout,
                _disposeCancellationTokenSource.Token);
        }
        catch (TimeoutException)
        {
            if (!completionSource.Task.IsCompleted)
            {
                _autoLaunchBetterGiPending = false;
                _initialConnectionRetriesRemaining = 0;
                CompleteConnectionFailure(CreateConnectionTimeoutFailure());
                TryDisconnectRdpHost();
            }
        }
        finally
        {
            if (ReferenceEquals(_connectionAttemptCompletionSource, completionSource))
            {
                _connectionAttemptCompletionSource = null;
            }
        }
    }

    public void ShowWindow()
    {
        ThrowIfDisposed();
        ShowDesktopWindow(EnsureDesktopWindow());
        RefreshState();
    }

    public void HideWindow()
    {
        ThrowIfDisposed();
        _desktopWindow?.Hide();
        RefreshState("Hid BetterGI Virtual Desktop window; RDP connection stays active");
    }

    public void ShowChildSessionDesktop()
    {
        ThrowIfDisposed();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendShowDesktopShortcut();
        RefreshState("Sent Win+D to BetterGI Virtual Desktop");
    }

    public void ShowChildSessionTaskView()
    {
        ThrowIfDisposed();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendTaskViewShortcut();
        RefreshState("Sent Win+Tab to BetterGI Virtual Desktop");
    }

    public void SetSmartSizing(bool enabled)
    {
        ThrowIfDisposed();
        EnsureDesktopWindow().RdpHost.SetSmartSizing(enabled);
        _config.SmartSizingEnabled = enabled;
        RefreshState(enabled ? "Window display mode switched to Fit" : "Window display mode switched to 1:1");
    }

    public void SetKeepAspectRatio(bool enabled)
    {
        ThrowIfDisposed();
        _config.KeepAspectRatio = enabled;
        RefreshState(enabled ? "Virtual Desktop window will lock aspect ratio" : "Virtual Desktop window unlocked aspect ratio");
    }

    public void SetTopmost(bool enabled)
    {
        ThrowIfDisposed();
        _config.TopmostEnabled = enabled;
        RefreshState(enabled ? "Virtual Desktop window pinned Always on Top" : "Virtual Desktop window unpinned from top");
    }

    public bool SetSendSystemShortcutsToRemote(bool enabled)
    {
        ThrowIfDisposed();
        if (SendSystemShortcutsToRemote == enabled)
        {
            return false;
        }

        SendSystemShortcutsToRemote = enabled;
        _config.SendSystemShortcutsToRemote = enabled;
        var window = EnsureDesktopWindow();
        window.RdpHost.SetSendSystemShortcutsToRemote(enabled);
        var target = enabled ? "Virtual Desktop" : "Local PC";
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.SystemShortcuts,
            $"System shortcut target changed to {target}");
    }

    public bool SetAudioMuted(bool muted)
    {
        ThrowIfDisposed();
        if (AudioMuted == muted)
        {
            return false;
        }

        _config.AudioMuted = muted;
        var window = EnsureDesktopWindow();
        window.RdpHost.SetAudioMuted(muted);
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.Audio,
            muted ? "Virtual Desktop audio muted" : "Virtual Desktop audio unmuted");
    }

    public void SetGameMouseModeEnabled(bool enabled)
    {
        ThrowIfDisposed();
        _instanceService.SetGameMouseModeEnabled(enabled);
        _config.GameMouseModeEnabled = enabled;
        RefreshState(enabled
            ? "Switched to Game Mouse mode"
            : "Switched to Standard Mouse mode");
    }

    public void SetWatchModeEnabled(bool enabled)
    {
        ThrowIfDisposed();
        _config.WatchModeEnabled = enabled;
        _desktopWindow?.RdpHost.SetInputBlocked(enabled);
        RefreshState(enabled
            ? "Entered View-Only mode; local mouse and keyboard will not pass into Virtual Desktop"
            : "Exited View-Only mode; Virtual Desktop input is active");
    }

    public Task LaunchBetterGiAsync()
    {
        ThrowIfDisposed();
        return LaunchBetterGiCoreAsync(isAutomatic: false);
    }

    public bool IsRelativeMouseForwardingAvailable()
    {
        return _desktopWindow?.IsVisible == true
               && _desktopWindow.RdpHost.IsInputWindowFocused();
    }

    public bool TryGetRelativeMouseCaptureBounds(out DrawingRectangle bounds)
    {
        bounds = DrawingRectangle.Empty;
        if (!IsRelativeMouseForwardingAvailable() || _desktopWindow is null)
        {
            return false;
        }

        var rdpHost = _desktopWindow.RdpHost;
        bounds = rdpHost.RectangleToScreen(rdpHost.ClientRectangle);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public async Task LaunchExecutableAsync(string executablePath)
    {
        ThrowIfDisposed();
        var childSessionId = GetRequiredChildSessionId();

        await _launchSemaphore.WaitAsync();
        try
        {
            RefreshState($"Launching {System.IO.Path.GetFileName(executablePath)} as administrator");
            await ChildSessionProcessLauncher.LaunchElevatedAsync(childSessionId, executablePath);
            RefreshState(
                $"Launched {System.IO.Path.GetFileName(executablePath)} as administrator in Virtual Desktop (Session {childSessionId})");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    public async Task LogoffAndHideAsync()
    {
        ThrowIfDisposed();
        _autoLaunchBetterGiPending = false;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);

        await _launchSemaphore.WaitAsync();
        try
        {
            TryDisconnectRdpHost();
            RefreshState("Disconnecting RDP and signing out of BetterGI Virtual Desktop");

            var terminatedSessionId = await Task.Run(ChildSessionNativeMethods.TerminateChildSession);
            _desktopWindow?.Hide();
            RefreshState(terminatedSessionId is null
                ? "No active Virtual Desktop session; Virtual Desktop window hidden"
                : $"Virtual Desktop session {terminatedSessionId.Value} signed out and window hidden");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    public void RefreshState(string? operationMessage = null)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            _lastOperationMessage = operationMessage;
        }

        try
        {
            var enabled = ChildSessionNativeMethods.IsChildSessionsEnabled();
            ChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            ConnectedState = _desktopWindow?.RdpHost.ConnectedState ?? 0;

            var connectionText = ConnectedState switch
            {
                0 => "Disconnected",
                1 => "Connected",
                2 => "Connecting",
                _ => $"Unknown connection state {ConnectedState}"
            };
            var sessionText = ChildSessionId?.ToString() ?? "None";
            var mainText = _lastOperationMessage ?? connectionText;

            StatusText =
                $"{mainText} | RDP: {connectionText} | Virtual Desktop Session: {sessionText} | Enabled: {enabled}";
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            StatusText = exception.GetBaseException().Message;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _disposeCancellationTokenSource.Cancel();
        _autoLaunchBetterGiPending = false;
        _connectionAttemptCompletionSource?.TrySetCanceled();
        _connectionAttemptCompletionSource = null;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;

        if (_desktopWindow is not null)
        {
            _desktopWindow.IsVisibleChanged -= OnDesktopWindowVisibilityChanged;
            _desktopWindow.RdpHost.ConnectionFailed -= OnRdpConnectionFailed;
            _desktopWindow.RdpHost.LoginCompleted -= OnRdpLoginCompleted;
            TryDisconnectRdpHost();
        }

        if (_instanceService.Context.IsRoot)
        {
            try
            {
                _ = ChildSessionNativeMethods.TerminateChildSession(wait: false);
            }
            catch (Exception exception) when (IsExpectedChildSessionException(exception))
            {
                // 应用正在退出，Child Session 清理失败不应阻止主程序关闭。
            }
        }

        if (_desktopWindow is not null)
        {
            _desktopWindow.AllowClose = true;
            _desktopWindow.Close();
            _desktopWindow = null;
        }

        _launchSemaphore.Dispose();
        _disposeCancellationTokenSource.Dispose();
    }

    private void ConnectCore(string operationMessage)
    {
        var existingSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);

        if (window.RdpHost.ConnectedState == 0)
        {
            _autoLaunchBetterGiPending = existingSessionId is null;
            _initialConnectionRetriesRemaining = _autoLaunchBetterGiPending
                ? InitialConnectionRetryCount
                : 0;
            window.RdpHost.ConnectToChildSession(DefaultDesktopSize);
        }

        RefreshState(operationMessage);
    }

    private ChildSessionWindow EnsureDesktopWindow()
    {
        if (_desktopWindow is not null)
        {
            return _desktopWindow;
        }

        _desktopWindow = _serviceProvider.GetRequiredService<ChildSessionWindow>();
        _desktopWindow.IsVisibleChanged += OnDesktopWindowVisibilityChanged;
        _desktopWindow.RdpHost.ConnectionFailed += OnRdpConnectionFailed;
        _desktopWindow.RdpHost.LoginCompleted += OnRdpLoginCompleted;
        _desktopWindow.RdpHost.SetSmartSizing(_config.SmartSizingEnabled);
        _desktopWindow.RdpHost.SetSendSystemShortcutsToRemote(SendSystemShortcutsToRemote);
        _desktopWindow.RdpHost.SetAudioMuted(AudioMuted);
        return _desktopWindow;
    }

    private static void ShowDesktopWindow(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_statusTickInProgress || _disposed)
        {
            return;
        }

        _statusTickInProgress = true;
        try
        {
            RefreshState();
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(exception.GetBaseException().Message);
        }
        finally
        {
            _statusTickInProgress = false;
        }
    }

    private async Task LaunchBetterGiCoreAsync(bool isAutomatic)
    {
        await _launchSemaphore.WaitAsync();
        try
        {
            var childSessionId = GetRequiredChildSessionId();
            RefreshState(isAutomatic
                ? "Virtual Desktop loaded; auto-launching BetterGI as administrator"
                : "Launching BetterGI as administrator");
            await ChildSessionProcessLauncher.LaunchBetterGiAsync(childSessionId);
            RefreshState(
                $"Launched BetterGI as administrator in Virtual Desktop (Session {childSessionId})");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>
    /// 取当前桌面分身会话 ID；没有可用会话时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    private uint GetRequiredChildSessionId()
    {
        var childSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSessionId is null)
        {
            throw new InvalidOperationException("No Virtual Desktop session is currently available. Please start Virtual Desktop first.");
        }

        return childSessionId.Value;
    }

    /// <summary>
    /// 用户主动请求检查时收集 Windows 环境结论，供问题诊断使用。
    /// 这些检查全部是只读的，不会修改系统设置。
    /// </summary>
    public void RefreshEnvironmentCheck()
    {
        ThrowIfDisposed();
        _environmentIssues = ChildSessionEnvironmentCheck.Collect();

        foreach (var issue in _environmentIssues)
        {
            _logger.LogWarning(
                "Virtual Desktop environment check: {IssueTitle}. {IssueSummary}",
                issue.Title,
                issue.Summary);
        }
    }

    private void EnsureChildSessionsEnabled()
    {
        if (!ChildSessionNativeMethods.IsChildSessionsEnabled())
        {
            ChildSessionNativeMethods.EnableChildSessions();
        }
    }

    private void OnDesktopWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshState();
    }

    private void OnRdpConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _lastConnectionFailure = e;

        if (_pendingRdpSettingChanges != RdpSettingChange.None)
        {
            if (_rdpSettingsReconnectRetryInProgress)
            {
                return;
            }

            if (_rdpSettingsReconnectRetriesRemaining > 0)
            {
                RetryRdpSettingsReconnectAsync(e);
                return;
            }

            _pendingRdpSettingChanges = RdpSettingChange.None;
        }

        if (_autoLaunchBetterGiPending
            && _initialConnectionRetriesRemaining > 0
            && !_connectionRetryInProgress)
        {
            RetryInitialConnectionAsync(e);
            return;
        }

        CompleteConnectionFailure(e);
    }

    // RDP 报告连接成功后稍作等待，让新 Child Session 的桌面初始化完成。
    // 使用 OnLoginComplete 等待实际登录完成，避免依赖固定时长的延时。
    private async void OnRdpLoginCompleted(object? sender, EventArgs e)
    {
        _lastConnectionFailure = null;
        _initialConnectionRetriesRemaining = 0;
        _connectionRetryInProgress = false;
        _connectionAttemptCompletionSource?.TrySetResult(true);

        // 输入窗口每次连接都会新建，观看模式需要重新施加到新窗口上。
        _desktopWindow?.RdpHost.SetInputBlocked(WatchModeEnabled);

        var completedSettingChanges = _pendingRdpSettingChanges;
        if (completedSettingChanges != RdpSettingChange.None)
        {
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _rdpSettingsReconnectRetryInProgress = false;
            RefreshState("Auto-reconnect completed; RDP settings applied");

            if (completedSettingChanges.HasFlag(RdpSettingChange.SystemShortcuts))
            {
                SystemShortcutsReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }

            if (completedSettingChanges.HasFlag(RdpSettingChange.Audio))
            {
                AudioReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            RefreshState("Virtual Desktop logon initialization completed");
        }

        if (!_autoLaunchBetterGiPending)
        {
            return;
        }

        _autoLaunchBetterGiPending = false;
        await Task.Yield();
        if (_disposed)
        {
            return;
        }

        try
        {
            RefreshState();
            if (ConnectedState == 1 && ChildSessionId is not null)
            {
                await LaunchBetterGiCoreAsync(isAutomatic: true);
            }
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState($"Failed to auto-launch BetterGI: {exception.GetBaseException().Message}");
        }
    }

    private async void RetryRdpSettingsReconnectAsync(
        ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _rdpSettingsReconnectRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = RdpSettingsReconnectRetryCount
                          - _rdpSettingsReconnectRetriesRemaining
                          + 1;
        _rdpSettingsReconnectRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << retryNumber);

        _logger.LogWarning(
            "RDP auto-reconnect after setting change failed; retry #{RetryNumber} in {RetryDelaySeconds}s. "
            + "Error: {ErrorMessage}, ErrorCode: {ErrorCode}, ExtendedErrorCode: {ExtendedErrorCode}",
            retryDelay.TotalSeconds,
            retryNumber,
            firstFailure.Message,
            firstFailure.ErrorCode,
            firstFailure.ExtendedErrorCode);
        RefreshState(
            $"RDP auto-reconnect did not succeed yet; retrying in {retryDelay.TotalSeconds:0}s"
            + $" ({retryNumber}/{RdpSettingsReconnectRetryCount})");

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || _pendingRdpSettingChanges == RdpSettingChange.None)
            {
                return;
            }

            RefreshState(
                $"Retrying RDP auto-reconnect ({retryNumber}/{RdpSettingsReconnectRetryCount})");
            _rdpSettingsReconnectRetryInProgress = false;
            EnsureDesktopWindow().RdpHost.ReconnectToChildSession(DefaultDesktopSize);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                $"RDP automatic reconnection failed: {actualException.Message}",
                errorCode);
        }
        finally
        {
            _rdpSettingsReconnectRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    private async void RetryInitialConnectionAsync(ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _connectionRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = InitialConnectionRetryCount - _initialConnectionRetriesRemaining + 1;
        _initialConnectionRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << (retryNumber - 1));

        _logger.LogWarning(
            "Virtual Desktop initial connection failed; retry #{RetryNumber} in {RetryDelaySeconds}s. "
            + "Error: {ErrorMessage}, ErrorCode: {ErrorCode}, ExtendedErrorCode: {ExtendedErrorCode}",
            retryDelay.TotalSeconds,
            retryNumber,
            firstFailure.Message,
            firstFailure.ErrorCode,
            firstFailure.ExtendedErrorCode);
        RefreshState(
            $"Virtual Desktop initial connection is not complete yet; auto-retrying in {retryDelay.TotalSeconds:0}s"
            + $" ({retryNumber}/{InitialConnectionRetryCount})");

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || !_autoLaunchBetterGiPending)
            {
                return;
            }

            RefreshState();
            if (_desktopWindow is null)
            {
                return;
            }

            RefreshState(
                $"Retrying Virtual Desktop connection ({retryNumber}/{InitialConnectionRetryCount})");
            _connectionRetryInProgress = false;
            // OnLogonError 触发后 ActiveX 可能仍处于连接状态，必须先断开再重连。
            // 否则这里直接退出会丢失已捕获的真实错误，最终只剩外层连接超时。
            _desktopWindow.RdpHost.ReconnectToChildSession(DefaultDesktopSize);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                $"Failed to retry Virtual Desktop connection: {actualException.Message}",
                errorCode);
        }
        finally
        {
            _connectionRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    /// <summary>
    /// 连接失败的统一收尾：「重试耗尽」与「连接超时」两条路径都经由这里附加诊断信息，
    /// 再更新状态并通知订阅者。
    /// </summary>
    private void CompleteConnectionFailure(ChildSessionConnectionFailedEventArgs e)
    {
        // 统一在这里补充诊断，让「重试耗尽」和「连接超时」两条失败路径给出同样的信息。
        var diagnosed = new ChildSessionConnectionFailedEventArgs(
            $"{e.Message}{BuildFailureDiagnosis()}",
            e.ErrorCode,
            e.ExtendedErrorCode);
        _lastConnectionFailure = diagnosed;
        _autoLaunchBetterGiPending = false;
        _initialConnectionRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);
        _logger.LogError(
            "Virtual Desktop RDP connection failed: {ErrorMessage}, ErrorCode: {ErrorCode}, ExtendedErrorCode: {ExtendedErrorCode}",
            diagnosed.Message,
            diagnosed.ErrorCode,
            diagnosed.ExtendedErrorCode);
        RefreshState(diagnosed.Message);
        ConnectionFailed?.Invoke(this, diagnosed);
    }

    /// <summary>
    /// 构造连接超时的失败信息，并附上 RDP ActiveX 最后报告的失败原因（如果有）。
    /// </summary>
    private ChildSessionConnectionFailedEventArgs CreateConnectionTimeoutFailure()
    {
        var timeoutMessage =
            $"Virtual Desktop connection and sign-in initialization did not complete within {ConnectionTimeout.TotalSeconds:0} seconds.";
        var lastDiagnostic =
            _desktopWindow?.RdpHost.LastConnectionDiagnostic
            ?? _lastConnectionFailure;
        if (lastDiagnostic is null)
        {
            return new ChildSessionConnectionFailedEventArgs(
                $"{timeoutMessage}\n\nRDP ActiveX did not report a more specific failure reason.",
                ErrorTimeout);
        }

        return new ChildSessionConnectionFailedEventArgs(
            $"{timeoutMessage}\n\nLast RDP ActiveX report:\n{lastDiagnostic.Message}",
            lastDiagnostic.ErrorCode,
            lastDiagnostic.ExtendedErrorCode);
    }

    /// <summary>
    /// 连接失败后的补充诊断：报告是否存在桌面分身会话，并附上已有的主动检查结论。
    /// 此处只读取缓存，不触发环境检查。
    /// RDP ActiveX 自身只回报「发生内部错误」和断开原因，单独看这些信息无法定位问题。
    /// </summary>
    private string BuildFailureDiagnosis()
    {
        var builder = new StringBuilder();

        if (ChildSessionNativeMethods.TryGetChildSessionId() is null)
        {
            builder.AppendLine().AppendLine();
            builder.Append(
                "Diagnosis: No Virtual Desktop session is available, meaning Windows could not establish the RDP session. "
                + "This state alone cannot determine the exact cause — both session creation failure and failed credential validation produce this result. "
                + "Please check the RDP ActiveX error message above together with diagnostics.");
        }

        if (_environmentIssues.Count > 0)
        {
            builder.AppendLine().AppendLine();
            for (var i = 0; i < _environmentIssues.Count; i++)
            {
                if (i > 0)
                {
                    builder.AppendLine();
                }

                var issue = _environmentIssues[i];
                builder.Append("- ")
                       .Append(BetterGenshinImpact.Service.I18n.I18nService.Instance.Translate(issue.Title))
                       .Append(": ")
                       .Append(BetterGenshinImpact.Service.I18n.I18nService.Instance.Translate(issue.Summary));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 尝试断开 RDP 主机连接。ActiveX 正在自行断开时可能返回 COM 错误，忽略后仍继续注销 Child Session。
    /// </summary>
    private void TryDisconnectRdpHost()
    {
        try
        {
            _desktopWindow?.RdpHost.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException)
        {
            // ActiveX 正在自行断开时可能返回 COM 错误，仍可继续注销 Child Session。
        }
    }

    private static bool IsExpectedChildSessionException(Exception exception)
    {
        return exception is Win32Exception
            or COMException
            or EntryPointNotFoundException
            or TargetInvocationException
            or InvalidCastException
            or InvalidOperationException
            or FileNotFoundException
            or ArgumentException;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool ReconnectForRdpSettingChange(
        ChildSessionWindow window,
        RdpSettingChange settingChange,
        string operationMessage)
    {
        RefreshState();
        if (window.RdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            RefreshState($"{operationMessage}; will apply on next RDP connection");
            return false;
        }

        var previousSettingChanges = _pendingRdpSettingChanges;
        _pendingRdpSettingChanges |= settingChange;
        _rdpSettingsReconnectRetriesRemaining = RdpSettingsReconnectRetryCount;
        try
        {
            window.RdpHost.ReconnectToChildSession(DefaultDesktopSize);
        }
        catch
        {
            _pendingRdpSettingChanges = previousSettingChanges;
            if (_pendingRdpSettingChanges == RdpSettingChange.None)
            {
                _rdpSettingsReconnectRetriesRemaining = 0;
            }

            throw;
        }

        RefreshState($"{operationMessage}; automatically reconnecting RDP");
        return true;
    }

    [Flags]
    private enum RdpSettingChange
    {
        None = 0,
        SystemShortcuts = 1,
        Audio = 2
    }
}
