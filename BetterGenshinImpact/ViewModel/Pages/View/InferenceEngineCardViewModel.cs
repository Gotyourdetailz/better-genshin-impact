using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Core.Recognition.ONNX;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterGenshinImpact.ViewModel.Pages.View;

public partial class InferenceEngineCardViewModel(InferenceDeviceType engine, InferenceEngineSettings settings) : ObservableObject
{
    public InferenceDeviceType Engine { get; } = engine;
    public string Name => Engine == InferenceDeviceType.Cpu ? "CPU" : Engine.ToString();
    public string Description => Engine switch
    {
        InferenceDeviceType.Cpu => "Built-in CPU inference with maximum compatibility.",
        InferenceDeviceType.DirectML => "Uses DirectML-compatible GPUs without extra compute libraries.",
        InferenceDeviceType.OpenVINO => "Uses Intel CPU, GPU, or NPU.",
        InferenceDeviceType.CUDA => "Uses NVIDIA GPU for inference.",
        _ => "Uses NVIDIA TensorRT optimized inference; initial model build may take longer."
    };
    public InferenceEngineSettings Settings { get; } = settings.Copy();
    [ObservableProperty] private ObservableCollection<InferenceDeviceInfo> _devices = [];
    public bool SupportsCache => Engine is InferenceDeviceType.OpenVINO or InferenceDeviceType.TensorRT;
    public bool IsCpu => Engine == InferenceDeviceType.Cpu;
    public bool IsRecommended => Engine == InferenceDeviceType.OpenVINO;
    [ObservableProperty] private InferenceDeviceInfo? _selectedDevice;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _hasFailure;
    [ObservableProperty] private string _statusText = "Checking...";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDetails))] private string _details = "";
    [ObservableProperty] private string _deviceMessage = "";
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _canCompleteDependencies;
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private string _runtimeDirectory = "";
    [ObservableProperty] private string _applyBlockReason = "Checking runtime and devices...";
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    partial void OnSelectedDeviceChanged(InferenceDeviceInfo? value) => Settings.DeviceId = value?.Id ?? "";

    public void UpdateDevices(IReadOnlyList<InferenceDeviceInfo> devices, string preferredId)
    {
        // 整体替换列表后最后恢复选择，避免 ComboBox 清空旧列表时把保存的设备编号覆盖为空。
        var supported = devices.DistinctBy(device => device.Id).ToArray();
        Devices = new ObservableCollection<InferenceDeviceInfo>(supported);
        SelectedDevice = supported.FirstOrDefault(device => device.Id == preferredId) ?? supported.FirstOrDefault();
    }
}
