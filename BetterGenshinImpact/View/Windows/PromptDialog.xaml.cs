using BetterGenshinImpact.Helpers.Ui;
using BetterGenshinImpact.Service.I18n;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterGenshinImpact.View.Windows;

/// <summary>
/// 对话框配置类，用于控制对话框中的元素显示
/// </summary>
public class PromptDialogConfig
{
    /// <summary>
    /// 是否显示左下角按钮
    /// </summary>
    public bool ShowLeftButton { get; set; } = false;

    /// <summary>
    /// 左下角按钮的文本
    /// </summary>
    public string LeftButtonText { get; set; } = "Action";

    /// <summary>
    /// 左下角按钮的点击事件
    /// </summary>
    public RoutedEventHandler? LeftButtonClick { get; set; }

}

public partial class PromptDialog
{
    private readonly PromptDialogConfig _config;

    public PromptDialog(string question, string title, UIElement uiElement, string? defaultValue, PromptDialogConfig? config = null)
    {
        InitializeComponent();
        MyTitleBar.Title = I18nService.Instance.Translate(title);
        TxtQuestion.Text = I18nService.Instance.Translate(question);
        _config = config ?? new PromptDialogConfig();

        TranslateElementTree(uiElement);
        DynamicContent.Content = uiElement;
        if (DynamicContent.Content is TextBox textBox && defaultValue != null)
        {
            textBox.Text = defaultValue;
        }
        else if (DynamicContent.Content is ComboBox comboBox)
        {
            AttachTranslatedItemTemplateIfNeeded(comboBox);
            if (defaultValue != null)
            {
                comboBox.Text = defaultValue;
            }
        }

        // 配置左下角按钮
        ConfigureLeftButton();

        this.Loaded += PromptDialogLoaded;
        this.SourceInitialized += PromptDialog_SourceInitialized;
    }

    private static void TranslateElementTree(object? element)
    {
        switch (element)
        {
            case null:
                return;
            case System.Windows.Controls.TextBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                textBlock.Text = I18nService.Instance.Translate(textBlock.Text);
                break;
            case Wpf.Ui.Controls.TextBox uiTextBox:
                if (!string.IsNullOrEmpty(uiTextBox.PlaceholderText))
                {
                    uiTextBox.PlaceholderText = I18nService.Instance.Translate(uiTextBox.PlaceholderText);
                }

                break;
            case Expander expander:
                if (expander.Header is string expanderHeader)
                {
                    expander.Header = I18nService.Instance.Translate(expanderHeader);
                }
                else
                {
                    TranslateElementTree(expander.Header);
                }

                TranslateElementTree(expander.Content);
                break;
            case ContentControl contentControl:
                if (contentControl.Content is string contentText)
                {
                    contentControl.Content = I18nService.Instance.Translate(contentText);
                }
                else
                {
                    TranslateElementTree(contentControl.Content);
                }

                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    TranslateElementTree(child);
                }

                break;
            case Decorator decorator:
                TranslateElementTree(decorator.Child);
                break;
        }

        if (element is ComboBox comboBox)
        {
            AttachTranslatedItemTemplateIfNeeded(comboBox);
        }

        if (element is ItemsControl itemsControl)
        {
            foreach (var item in itemsControl.Items)
            {
                TranslateElementTree(item);
            }
        }
    }

    private static void AttachTranslatedItemTemplateIfNeeded(ComboBox comboBox)
    {
        if (comboBox.ItemTemplate != null || !string.IsNullOrEmpty(comboBox.DisplayMemberPath))
        {
            return;
        }

        var factory = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        factory.SetValue(System.Windows.Controls.TextBlock.TextProperty, new TValueExtension());
        comboBox.ItemTemplate = new DataTemplate { VisualTree = factory };
    }

    private void PromptDialog_SourceInitialized(object? sender, EventArgs e)
    {
        // 应用与主窗口相同的背景主题
        WindowHelper.TryApplySystemBackdrop(this);
    }

    private void ConfigureLeftButton()
    {
        if (_config.ShowLeftButton)
        {
            BtnLeftBottom.Content = I18nService.Instance.Translate(_config.LeftButtonText);
            if (_config.LeftButtonClick != null)
            {
                BtnLeftBottom.Click += _config.LeftButtonClick;
            }
        }
        else
        {
            BtnLeftBottom.Visibility = Visibility.Collapsed;
        }
    }

    private void PromptDialogLoaded(object sender, RoutedEventArgs e)
    {
        DynamicContent.Focus();
    }

    public static string Prompt(string question, string title, string defaultValue = "", PromptDialogConfig? config = null)
    {
        var textBox = new TextBox
        {
            VerticalAlignment = VerticalAlignment.Top,
        };
        var inst = new PromptDialog(question, title, textBox, defaultValue, config);
        inst.ShowDialog();
        return inst.DialogResult == true ? inst.ResponseText : "";
    }

    public static string Prompt(string question, string title, UIElement uiElement, string defaultValue = "", PromptDialogConfig? config = null)
    {
        var inst = new PromptDialog(question, title, uiElement, defaultValue, config);
        inst.ShowDialog();
        return inst.DialogResult == true ? inst.ResponseText : "";
    }

    public static string Prompt(string question, string title, UIElement uiElement, Size size, PromptDialogConfig? config = null)
    {
        var inst = new PromptDialog(question, title, uiElement, "", config)
        {
            Width = size.Width,
            Height = size.Height
        };
        inst.ShowDialog();
        return inst.DialogResult == true ? inst.ResponseText : "";
    }

    public string ResponseText
    {
        get
        {
            if (DynamicContent.Content is TextBox textBox)
            {
                return textBox.Text;
            }
            else if (DynamicContent.Content is ComboBox comboBox)
            {
                return comboBox.Text;
            }
            else
            {
                return "true";
            }
        }
    }

    private void BtnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
