using BetterGenshinImpact.Service.I18n;
using Wpf.Ui.Violeta.Controls;

namespace BetterGenshinImpact;

internal static class Toast
{
    public static void Information(string message, ToastLocation location = ToastLocation.TopCenter, int time = 2000)
        => Wpf.Ui.Violeta.Controls.Toast.Information(I18nService.Instance.Translate(message), location, time: time);

    public static void Success(string message, ToastLocation location = ToastLocation.TopCenter, int time = 2000)
        => Wpf.Ui.Violeta.Controls.Toast.Success(I18nService.Instance.Translate(message), location, time: time);

    public static void Warning(string message, ToastLocation location = ToastLocation.TopCenter, int time = 2000)
        => Wpf.Ui.Violeta.Controls.Toast.Warning(I18nService.Instance.Translate(message), location, time: time);

    public static void Error(string message, ToastLocation location = ToastLocation.TopCenter, int time = 2000)
        => Wpf.Ui.Violeta.Controls.Toast.Error(I18nService.Instance.Translate(message), location, time: time);

    public static void Question(string message, ToastLocation location = ToastLocation.TopCenter, int time = 2000)
        => Wpf.Ui.Violeta.Controls.Toast.Question(I18nService.Instance.Translate(message), location, time: time);
}
