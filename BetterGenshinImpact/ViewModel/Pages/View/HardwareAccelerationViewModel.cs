using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterGenshinImpact.ViewModel.Pages.View;

public sealed record InferenceThreadChoice(int Count, string Name);
public sealed record PaddleOcrCpuModeChoice(PaddleOcrCpuMode Mode, string Name, string Description);

public partial class HardwareAccelerationViewModel : ObservableObject, IViewModel, IDisposable
{
    private readonly InferenceRuntimeService _runtime;
    private readonly IOnnxRuntimeInstaller _installer;
    private readonly IInferenceDeviceDiscovery _devices;
    private readonly IInferenceModelDiagnostics _diagnostics;
    private readonly BgiOnnxFactory _factory;
    private readonly IConfigService _configService;
    private readonly IInferenceBenchmarkLeaseProvider _leases;
    private readonly IInferenceSettingsInteraction _interaction;
    private readonly DispatcherTimer _taskStatusTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private Task _operation = Task.CompletedTask;
    private bool _disposed;

    public ObservableCollection<InferenceEngineCardViewModel> Engines { get; } = [];
    public ObservableCollection<InferenceBenchmarkResult> Results { get; } = [];
    public InferenceThreadChoice[] CpuThreadChoices { get; }
    public int MaxCpuThreads => Environment.ProcessorCount;
    public PaddleOcrCpuModeChoice[] OcrCpuModeChoices { get; } =
    [
        new(PaddleOcrCpuMode.FollowGlobal, "Follow Global Settings", "Use the global thread count from the CPU card."),
        new(PaddleOcrCpuMode.RuntimeAuto, "Runtime Auto-Schedule", "Leave thread parameters unspecified and let ONNX Runtime decide."),
        new(PaddleOcrCpuMode.GameplayPriority, "Gameplay Smoothness Priority", "Limit parallel OCR threads to reduce game frame drops."),
        new(PaddleOcrCpuMode.Custom, "Custom Settings", "Configure thread counts for text detection, recognition, and graph nodes individually.")
    ];
    public PaddleOcrCpuConfig PaddleOcrCpu { get; }
    public bool IsOcrCpuCustom => PaddleOcrCpu.Mode == PaddleOcrCpuMode.Custom;
    public bool IsOcrCpuConfigured => PaddleOcrCpu.Mode is PaddleOcrCpuMode.GameplayPriority or PaddleOcrCpuMode.Custom;
    public string OcrCpuPreview => PaddleOcrCpu.ToOptions().Describe(CpuThreads);
    public string OcrCpuApplicability => ForceCpuOcr || SelectedEngine?.IsCpu == true
        ? "Used for PPOCR CPU detection and recognition sessions after applying and restarting."
        : "Currently OCR follows the selected engine; these parameters are saved and apply when PPOCR uses CPU.";
    public string OcrCpuModeHint => $"Available logical processors: {Environment.ProcessorCount}. " + (PaddleOcrCpu.Mode switch
    {
        PaddleOcrCpuMode.GameplayPriority => "Text detection uses half of logical processors; text recognition uses one quarter (clamped to 2-4 threads). Both models run nodes sequentially to reduce OCR CPU spikes while maintaining performance.",
        PaddleOcrCpuMode.Custom => "Configure compute threads for text detection and recognition separately. 0 lets ONNX Runtime choose automatically; graph node threads only apply when parallel graph execution is enabled.",
        PaddleOcrCpuMode.RuntimeAuto => "Does not specify OCR thread counts, execution mode, or thread spinning strategy, leaving scheduling entirely to ONNX Runtime.",
        _ => "OCR shares the global CPU thread count; 0 lets ONNX Runtime choose automatically."
    });
    public string CurrentRuntimeText => _runtime.Current.Description;
    public string CurrentEngineName => _runtime.Current.Engine == InferenceDeviceType.Cpu ? "CPU" : _runtime.Current.Engine.ToString();
    public string CurrentOcrText => _runtime.Current.OcrDescription;
    public string CurrentValidationText => _runtime.Current.Engine == InferenceDeviceType.Cpu
        ? "Built-in CPU · No extra validation needed"
        : _runtime.IsCurrentValidated ? "Model compatibility verified" : "Active · Model compatibility check pending";
    private HardwareAccelerationConfig Config => _configService.Get().HardwareAccelerationConfig;
    public bool HasPendingChanges => _runtime.HasPendingRestart;
    public string PendingText
    {
        get
        {
            if (!HasPendingChanges) return "";
            var card = Engines.FirstOrDefault(c => c.Engine == Config.InferenceDevice) ?? Engines.First(c => c.IsCpu);
            var savedId = Config.GetEngineSettings(Config.InferenceDevice).DeviceId;
            var device = card.Devices.FirstOrDefault(d => d.Id == savedId)?.Name ?? savedId;
            return $"{card.Name} · {device}";
        }
    }
    public bool HasTreeImage => _diagnostics.HasDomainTreeImage;
    public string TreeImageText => HasTreeImage ? "Full Irminsul Tree Detect · 1 warmup, 10 measured runs" : "Test image unavailable";
    public string TreeBenchmarkEmptyText => HasTreeImage ? "No benchmark results yet" : "Full Detect timing available when test image is provided.";
    public string ValidationHint => HasTreeImage
        ? "After restarting, an Irminsul tree detection test will run to verify the selected runtime and device; falls back to CPU on failure."
        : "After restarting, an OCR model test will run to verify the selected runtime and device; falls back to CPU on failure.";
    public bool CanApply => !IsBusy && SelectedEngine is { SelectedDevice: not null, IsInstalled: true }
        && (HasDraftChanges || HasPendingChanges);
    public string ApplyButtonText => HasPendingChanges && !HasDraftChanges ? "Restart Now" : "Apply & Restart";
    public string SelectionText => SelectedEngine == null ? "Please select an inference engine" : $"{SelectedEngine.Name} · {SelectedEngine.SelectedDevice?.Name ?? "No available device"}";
    public string ApplyHint => IsBusy ? "Please wait for the current operation to finish"
        : SelectedEngine == null ? "Click a card to select an engine"
        : !SelectedEngine.IsInstalled || SelectedEngine.SelectedDevice == null ? SelectedEngine.ApplyBlockReason
        : HasDraftChanges ? "Not applied yet · Restart immediately or later after saving"
        : HasPendingChanges ? "Settings saved · Restart required to take effect"
        : "Matches current active settings";
    public string BenchmarkHint => !_leases.IsAvailable ? "Benchmarks do not run automatically; stop the running task to benchmark manually."
        : "Benchmarks do not run automatically; click to run 1 warmup and 10 measured iterations using the current active configuration.";
    public InferenceBenchmarkResult? OcrResult => Results.FirstOrDefault(r => r.Scene == "OCR");
    public InferenceBenchmarkResult? TreeResult => Results.FirstOrDefault(r => r.Scene is "古树检测" or "Irminsul Tree Detection");
    public bool HasOcrResult => OcrResult != null;
    public bool HasTreeResult => TreeResult != null;
    private bool HasDraftChanges
    {
        get
        {
            if (SelectedEngine?.SelectedDevice == null) return false;
            if (HasPendingChanges)
            {
                var saved = Config.GetEngineSettings(Config.InferenceDevice);
                return SelectedEngine.Engine != Config.InferenceDevice || SelectedEngine.SelectedDevice.Id != saved.DeviceId
                    || ForceCpuOcr != Config.CpuOcr || CpuThreads != Config.Cpu.CpuThreads
                    || PaddleOcrCpu.ToOptions() != Config.PaddleOcrCpu.ToOptions()
                    || (SelectedEngine.SupportsCache && SelectedEngine.Settings.EnableModelCache != saved.EnableModelCache);
            }
            var current = _runtime.Current;
            return SelectedEngine.Engine != current.Engine || SelectedEngine.SelectedDevice.Id != current.Device.Id
                || ForceCpuOcr != current.CpuOcr || CpuThreads != current.CpuThreads
                || PaddleOcrCpu.ToOptions() != current.PaddleOcrCpu
                || (SelectedEngine.SupportsCache && SelectedEngine.Settings.EnableModelCache != current.EnableModelCache)
                || (!SelectedEngine.IsCpu && !string.Equals(SelectedEngine.RuntimeDirectory, current.RuntimeDirectory, StringComparison.OrdinalIgnoreCase));
        }
    }
    public bool CanBenchmark => !IsBusy && _leases.IsAvailable;
    public bool CanBenchmarkTree => CanBenchmark && HasTreeImage;
    public bool CanInteract => !IsBusy;

    [ObservableProperty] private InferenceEngineCardViewModel? _selectedEngine;
    [ObservableProperty] private bool _forceCpuOcr;
    [ObservableProperty] private int _cpuThreads;
    [ObservableProperty] private bool _isOcrAdvancedExpanded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOperationMessage))] private string _progressText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _errorDetails = "";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorDetails);
    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(ProgressText);

    public HardwareAccelerationViewModel(InferenceRuntimeService runtime, IOnnxRuntimeInstaller installer,
        IInferenceDeviceDiscovery devices, IInferenceModelDiagnostics diagnostics, BgiOnnxFactory factory, IConfigService configService,
        IInferenceBenchmarkLeaseProvider leases, IInferenceSettingsInteraction interaction)
    {
        _runtime = runtime;
        _installer = installer;
        _devices = devices;
        _diagnostics = diagnostics;
        _factory = factory;
        _configService = configService;
        _leases = leases;
        _interaction = interaction;
        _forceCpuOcr = Config.CpuOcr;
        _cpuThreads = Config.Cpu.CpuThreads;
        PaddleOcrCpu = PaddleOcrCpuConfig.FromOptions(Config.PaddleOcrCpu.ToOptions());
        _isOcrAdvancedExpanded = false;
        PaddleOcrCpu.PropertyChanged += OcrCpuSettingsChanged;
        CpuThreadChoices = Enumerable.Range(0, Environment.ProcessorCount + 1)
            .Select(i => new InferenceThreadChoice(i, i == 0 ? "Auto (ORT Default)" : i.ToString())).ToArray();
        foreach (var engine in Enum.GetValues<InferenceDeviceType>().Where(engine => engine.IsSelectable()))
        {
            var card = new InferenceEngineCardViewModel(engine, Config.GetEngineSettings(engine));
            card.PropertyChanged += CardChanged;
            card.Settings.PropertyChanged += CardSettingsChanged;
            Engines.Add(card);
        }
        // TensorRT 已下线：历史配置可能在启动准备之前仍是 TRT，这里回退到 CPU 卡片而不是直接抛异常。
        SelectedEngine = Engines.FirstOrDefault(card => card.Engine == Config.InferenceDevice)
            ?? Engines.First(card => card.IsCpu);
        _taskStatusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => RefreshAvailability(), Dispatcher.CurrentDispatcher);
        _runtime.SettingsChanged += RuntimeSettingsChanged;
    }

    private void CardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InferenceEngineCardViewModel.SelectedDevice))
        {
            if (!IsBusy) SelectedEngine = (InferenceEngineCardViewModel)sender!;
            RefreshAvailability();
        }
    }
    private void CardSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsBusy && e.PropertyName == nameof(InferenceEngineSettings.EnableModelCache))
            SelectedEngine = Engines.First(card => ReferenceEquals(card.Settings, sender));
        RefreshAvailability();
    }
    partial void OnForceCpuOcrChanged(bool value) => RefreshAvailability();
    partial void OnCpuThreadsChanged(int value) => RefreshAvailability();
    private void OcrCpuSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaddleOcrCpuConfig.Mode) && PaddleOcrCpu.Mode == PaddleOcrCpuMode.Custom)
            IsOcrAdvancedExpanded = true;
        OnPropertyChanged(nameof(IsOcrCpuCustom));
        OnPropertyChanged(nameof(IsOcrCpuConfigured)); OnPropertyChanged(nameof(OcrCpuModeHint));
        RefreshAvailability();
    }
    private void RuntimeSettingsChanged(object? sender, EventArgs e)
    {
        if (!_taskStatusTimer.Dispatcher.CheckAccess())
        {
            _taskStatusTimer.Dispatcher.BeginInvoke(() => RuntimeSettingsChanged(sender, e));
            return;
        }
        if (_disposed) return;
        OnPropertyChanged(nameof(CurrentRuntimeText)); OnPropertyChanged(nameof(CurrentEngineName));
        OnPropertyChanged(nameof(CurrentOcrText));
        OnPropertyChanged(nameof(CurrentValidationText));
        OnPropertyChanged(nameof(HasPendingChanges)); OnPropertyChanged(nameof(PendingText));
        foreach (var card in Engines)
        {
            card.IsCurrent = card.Engine == _runtime.Current.Engine;
            card.HasFailure = Config.LastFailedEngine == card.Engine && !string.IsNullOrWhiteSpace(Config.LastValidationError)
                && !(card.IsCurrent && _runtime.IsCurrentValidated);
            if (card.IsCurrent && card.IsInstalled)
            {
                card.StatusText = card.IsCpu ? "Built-in · No install needed" : (_runtime.IsCurrentValidated ? "Model verified" : "Active · Unverified");
                if (!card.HasFailure) card.Details = "Runtime is ready.\n\nRuntime Directory:\n" + card.RuntimeDirectory;
            }
            else if (card.HasFailure)
            {
                card.StatusText = "Last validation failed";
                card.Details = Config.LastValidationError + "\n\nRuntime Directory:\n" + card.RuntimeDirectory;
            }
            else if (card.IsInstalled && string.IsNullOrWhiteSpace(card.ApplyBlockReason))
                card.StatusText = Config.ValidatedEngines.Contains(card.Engine) ? "Previously verified" : "Installed · Unverified";
        }
        RefreshAvailability();
    }
    partial void OnSelectedEngineChanged(InferenceEngineCardViewModel? value)
    {
        foreach (var card in Engines) card.IsSelected = card == value;
        RefreshAvailability();
    }
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract)); RefreshAvailability();
    }
    private void RefreshAvailability()
    {
        OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(CanBenchmark)); OnPropertyChanged(nameof(CanBenchmarkTree));
        OnPropertyChanged(nameof(ApplyHint)); OnPropertyChanged(nameof(ApplyButtonText)); OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(BenchmarkHint));
        OnPropertyChanged(nameof(OcrCpuPreview)); OnPropertyChanged(nameof(OcrCpuApplicability));
    }

    [RelayCommand]
    private async Task ShowEngineDetailsAsync(InferenceEngineCardViewModel card)
        => await ThemedMessageBox.ShowAsync($"{card.StatusText}\n\n{card.Details}\n\n{card.DeviceMessage}",
            $"{card.Name} · Runtime Info", MessageBoxButton.OK,
            card.HasFailure ? ThemedMessageBox.MessageBoxIcon.Warning : ThemedMessageBox.MessageBoxIcon.Information);

    [RelayCommand]
    private async Task ShowErrorAsync() => await ThemedMessageBox.ShowAsync(ErrorDetails, "Operation Details", MessageBoxButton.OK, ThemedMessageBox.MessageBoxIcon.Error);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(token => RefreshCoreAsync(token));

    private async Task RefreshCoreAsync(CancellationToken token, InferenceEngineCardViewModel? updatedCard = null)
    {
        ProgressText = "Detecting runtime and devices...";
        // 下载完成只刷新变更的引擎，避免其他运行库的探测拖住“应用并重启”。手动检测优先处理所选卡片。
        var cards = updatedCard != null ? new[] { updatedCard } : Engines.OrderByDescending(card => card == SelectedEngine).ToArray();
        foreach (var card in cards)
        {
            var installation = _installer.Inspect(card.Engine);
            card.RuntimeDirectory = installation.Directory;
            card.IsInstalled = installation.State == InferenceInstallationState.Installed;
            card.ApplyBlockReason = installation.State switch
            {
                InferenceInstallationState.Missing => "Please download the runtime before applying settings",
                InferenceInstallationState.MissingDependencies => "Runtime installed; click 'Install Dependencies' to complete setup",
                _ => "Detecting supported devices..."
            };
            card.CanDownload = installation.State == InferenceInstallationState.Missing && !card.IsCpu;
            card.CanCompleteDependencies = installation.State == InferenceInstallationState.MissingDependencies;
            card.IsCurrent = card.Engine == _runtime.Current.Engine;
            card.HasFailure = Config.LastFailedEngine == card.Engine && Config.LastValidationError.Length > 0
                && !(card.IsCurrent && _runtime.IsCurrentValidated);
            card.Details = installation.Description + "\n\nRuntime Directory:\n" + installation.Directory;
            card.StatusText = installation.State == InferenceInstallationState.Missing ? "DLL Not Installed" : "Missing Dependencies";
            if (card.HasFailure)
            {
                card.StatusText = "Last validation failed";
                card.Details += "\n\nLast Validation Result (History):\n" + Config.LastValidationError;
            }
            else if (card.IsCurrent && card.IsInstalled)
                card.StatusText = card.IsCpu ? "Built-in · No install needed" : (_runtime.IsCurrentValidated ? "Model verified" : "Active · Unverified");
            else if (card.IsInstalled)
                card.StatusText = Config.ValidatedEngines.Contains(card.Engine) ? "Previously verified" : "Installed · Unverified";
            var preferred = card.SelectedDevice?.Id ?? card.Settings.DeviceId;
            try
            {
                var found = await Task.Run(() => _devices.GetDevices(card.Engine, installation.Directory), token);
                card.UpdateDevices(found, preferred);
                if (card.IsInstalled) card.ApplyBlockReason = found.Count == 0 ? "No supported devices found; see card details" : "";
                card.DeviceMessage = found.Count == 0 ? (card.IsInstalled ? "No supported devices found; see details." : "Devices will be detected automatically after installation.") : $"Found {found.Count} supported device(s)";
                if (card.IsInstalled && found.Count == 0) card.StatusText = "No supported devices found";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                card.UpdateDevices([], preferred);
                card.ApplyBlockReason = "Device detection failed; see card details";
                card.HasFailure = true;
                card.StatusText = "Device detection failed";
                card.DeviceMessage = "Check details, device drivers, and runtime.";
                card.Details += "\n\nDevice Detection Error:\n" + e;
            }
        }
        OnPropertyChanged(nameof(HasTreeImage)); OnPropertyChanged(nameof(TreeImageText)); OnPropertyChanged(nameof(TreeBenchmarkEmptyText));
        OnPropertyChanged(nameof(ValidationHint)); OnPropertyChanged(nameof(PendingText));
        ProgressText = "Runtime and device information updated.";
        await Task.Run(() => _installer.CleanupInstalledDownloadsAsync(token), token);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply || SelectedEngine == null) return;
        await RunAsync(async token =>
        {
            ProgressText = "Saving inference settings...";
            if (HasDraftChanges) _runtime.Apply(SelectedEngine.Engine, SelectedEngine.Settings, ForceCpuOcr, CpuThreads, PaddleOcrCpu.ToOptions());
            ProgressText = "Settings saved; restart required to take effect.";
            await OfferRestartAsync();
        });
    }

    [RelayCommand]
    private async Task UseCpuAsync()
    {
        if (IsBusy) return;
        SelectedEngine = Engines.First(card => card.IsCpu);
        ForceCpuOcr = true; CpuThreads = 0;
        var defaults = new PaddleOcrCpuOptions().Normalize();
        PaddleOcrCpu.Mode = defaults.Mode;
        PaddleOcrCpu.DetectionThreads = defaults.DetectionThreads;
        PaddleOcrCpu.RecognitionThreads = defaults.RecognitionThreads;
        PaddleOcrCpu.InterOpThreads = defaults.InterOpThreads;
        PaddleOcrCpu.ParallelExecution = defaults.ParallelExecution;
        PaddleOcrCpu.AllowSpinning = defaults.AllowSpinning;
        await ApplyAsync();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (!IsBusy && HasPendingChanges) await RunAsync(_ => OfferRestartAsync());
    }

    private async Task OfferRestartAsync()
    {
        if (await _interaction.ConfirmRestartAsync(PendingText, ValidationHint, CurrentEngineName, !_leases.IsAvailable)
            && !_lifetime.IsCancellationRequested) _interaction.Restart();
    }

    [RelayCommand]
    private async Task DownloadRuntimeAsync(InferenceEngineCardViewModel card) => await DownloadAsync(card, false);
    [RelayCommand]
    private async Task CompleteDependenciesAsync(InferenceEngineCardViewModel card) => await DownloadAsync(card, true);

    private async Task DownloadAsync(InferenceEngineCardViewModel card, bool dependencies)
    {
        if (IsBusy) return;
        SelectedEngine = card;
        await RunAsync(async token =>
        {
            ProgressText = "Fetching download manifest...";
            var packages = await _installer.GetDownloadPlanAsync(card.Engine, dependencies, token);
            if (packages.Count == 0) { await RefreshCoreAsync(token, card); ProgressText = "Required components are already installed; device info updated."; return; }
            if (!await _interaction.ConfirmDownloadAsync(packages, dependencies)) return;
            token.ThrowIfCancellationRequested();
            var progress = new Progress<RuntimeDownloadProgress>(p =>
            {
                ProgressText = p.Message; ProgressValue = p.Percentage; IsProgressIndeterminate = p.Total <= 0;
            });
            await Task.Run(() => _installer.InstallAsync(card.Engine, packages, progress, token), token);
            // 安装、识别与按钮更新属于同一次操作，不能在 IsBusy 切换的间隙丢掉刷新。
            await RefreshCoreAsync(token, card);
            ProgressText = card.IsInstalled && card.SelectedDevice != null
                ? $"{card.Name} is ready. Click 'Apply & Restart' to activate."
                : card.ApplyBlockReason;
        });
    }

    [RelayCommand] private async Task BenchmarkOcrAsync() => await BenchmarkAsync(InferenceBenchmarkScene.Ocr);
    [RelayCommand] private async Task BenchmarkTreeAsync() => await BenchmarkAsync(InferenceBenchmarkScene.DomainTree);
    private async Task BenchmarkAsync(InferenceBenchmarkScene scene)
    {
        if (!CanBenchmark || (scene == InferenceBenchmarkScene.DomainTree && !HasTreeImage)) return;
        await RunAsync(async token =>
        {
            var progress = new Progress<InferenceBenchmarkProgress>(p =>
            {
                ProgressText = $"{p.Scene} · {p.Completed}/{p.Total}";
                ProgressValue = 100d * p.Completed / p.Total;
                IsProgressIndeterminate = false;
            });
            var result = await _diagnostics.BenchmarkAsync(scene, _factory.ForCurrentRuntime(), progress, token);
            foreach (var previous in Results.Where(r => r.Scene == result.Scene).ToArray()) Results.Remove(previous);
            Results.Add(result);
            OnPropertyChanged(nameof(OcrResult)); OnPropertyChanged(nameof(TreeResult));
            OnPropertyChanged(nameof(HasOcrResult)); OnPropertyChanged(nameof(HasTreeResult));
            ProgressText = "Benchmark complete (using active configuration).";
        });
    }

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) return Task.CompletedTask;
        IsBusy = true; ErrorDetails = ""; ProgressValue = 0; IsProgressIndeterminate = true;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return _operation = ExecuteAsync(action, _operationCancellation);
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationTokenSource cancellation)
    {
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { ProgressText = "Operation cancelled; test resources released and download progress saved."; }
        catch (Exception e) { ProgressText = "Operation failed: " + e.Message; ErrorDetails = e.ToString(); }
        finally
        {
            _operationCancellation = null; cancellation.Dispose(); IsBusy = false; IsProgressIndeterminate = false;
        }
    }
    [RelayCommand] private void Cancel() { _operationCancellation?.Cancel(); ProgressText = "Cancelling, waiting for current operation to finish safely..."; }

    [RelayCommand]
    private void OpenRuntimeFolder()
    {
        try
        {
            Directory.CreateDirectory(_installer.PluginsDirectory);
            using var process = Process.Start("explorer.exe", _installer.PluginsDirectory);
        }
        catch (Exception e) { ThemedMessageBox.Show($"Failed to open runtime directory: {e.Message}", "Open Directory Failed"); }
    }
    [RelayCommand]
    public void OnOpenCacheFolder()
    {
        Directory.CreateDirectory(ResolvedInferenceRuntime.ModelCacheDirectory);
        using var process = Process.Start("explorer.exe", ResolvedInferenceRuntime.ModelCacheDirectory);
    }

    public async Task StopAsync() { _lifetime.Cancel(); await _operation; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _taskStatusTimer.Stop();
        _runtime.SettingsChanged -= RuntimeSettingsChanged;
        PaddleOcrCpu.PropertyChanged -= OcrCpuSettingsChanged;
        foreach (var card in Engines)
        {
            card.PropertyChanged -= CardChanged;
            card.Settings.PropertyChanged -= CardSettingsChanged;
        }
        _lifetime.Dispose();
    }
}
