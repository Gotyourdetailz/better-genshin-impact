using BetterGenshinImpact.Service.I18n;
using System;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace BetterGenshinImpact.Service;

/// <summary>
/// Decorates <see cref="ISnackbarService"/> so every snackbar title and message
/// is automatically translated to English.
/// </summary>
public sealed class TranslatingSnackbarService : ISnackbarService
{
    private readonly ISnackbarService _inner;

    public TranslatingSnackbarService(ISnackbarService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public TimeSpan DefaultTimeOut
    {
        get => _inner.DefaultTimeOut;
        set => _inner.DefaultTimeOut = value;
    }

    public void SetSnackbarPresenter(SnackbarPresenter? contentPresenter)
    {
        _inner.SetSnackbarPresenter(contentPresenter);
    }

    public SnackbarPresenter? GetSnackbarPresenter()
    {
        return _inner.GetSnackbarPresenter();
    }

    public void Show(
        string title,
        string message,
        ControlAppearance appearance,
        IconElement? icon,
        TimeSpan timeout)
    {
        var translatedTitle = I18nService.Instance.Translate(title);
        var translatedMessage = I18nService.Instance.Translate(message);
        _inner.Show(translatedTitle, translatedMessage, appearance, icon, timeout);
    }
}
