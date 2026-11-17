using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.View.Windows;

namespace BetterGenshinImpact.ViewModel.Pages.View;

public interface IInferenceSettingsInteraction
{
    Task<bool> ConfirmDownloadAsync(IReadOnlyList<RuntimeDownloadPackage> packages, bool dependencies);
    Task<bool> ConfirmRestartAsync(string pending, string validationHint, string currentEngine, bool interruptsTask);
    void Restart();
}

/// <summary>设置页交互与安装、验证流程分离，测试不打开窗口、不重启程序。</summary>
public sealed class InferenceSettingsInteraction : IInferenceSettingsInteraction
{
    public async Task<bool> ConfirmDownloadAsync(IReadOnlyList<RuntimeDownloadPackage> packages, bool dependencies)
    {
        var components = string.Join("\n", packages.Select(p => $"• {p.Name}: {p.DownloadBytes / 1048576d:F0} MB"));
        var required = packages.Sum(p => p.DownloadBytes + p.InstalledBytes) / 1073741824d;
        return await ThemedMessageBox.ShowAsync($"{components}\n\nEstimated download: {packages.Sum(p => p.DownloadBytes) / 1048576d:F0} MB\nRequired disk space (including temp): up to {required:F1} GB\nExisting downloads will resume; installation does not automatically switch engines.\nDownload now?",
            dependencies ? "Install Dependencies" : "Download Runtime", MessageBoxButton.YesNo,
            ThemedMessageBox.MessageBoxIcon.Question) == MessageBoxResult.Yes;
    }

    public async Task<bool> ConfirmRestartAsync(string pending, string validationHint, string currentEngine, bool interruptsTask)
    {
        var warning = interruptsTask ? "The running task will be interrupted.\n" : "";
        return await ThemedMessageBox.ShowAsync($"{warning}Settings saved: {pending}\nRestart now?\n{validationHint}\nSelect 'No' to restart later and continue using {currentEngine} for this session.",
            "Inference Settings", MessageBoxButton.YesNo, ThemedMessageBox.MessageBoxIcon.Question) == MessageBoxResult.Yes;
    }

    public void Restart() => SystemControl.RestartApplication([], graceful: true);
}
