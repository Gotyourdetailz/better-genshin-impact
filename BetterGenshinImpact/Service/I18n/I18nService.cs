using BetterGenshinImpact.Core.Config;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace BetterGenshinImpact.Service.I18n;

/// <summary>
/// English-locked UI localization service.
/// </summary>
public sealed class I18nService : INotifyPropertyChanged
{
    public const string DefaultLanguage = "en";

    private readonly string _i18nDirectory = Global.Absolute(Path.Combine("User", "I18n"));
    private IReadOnlyDictionary<string, string> _translations = new Dictionary<string, string>();
    private KeyValuePair<string, string>[] _sortedPhrasePairs = [];
    private readonly ConcurrentDictionary<string, string> _compositeCache = new(StringComparer.Ordinal);
    private long _revision;

    private I18nService()
    {
        ChangeLanguage(DefaultLanguage);
    }

    public static I18nService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 供 TExtension 创建的 Binding 监听。语言改变时递增，触发转换器重新取值。
    /// </summary>
    public long Revision => _revision;

    public void ChangeLanguage(string? language)
    {
        language = DefaultLanguage;
        _translations = LoadTranslations(language);
        _sortedPhrasePairs = _translations
            .Where(kv => kv.Key.Length >= 2 && !string.IsNullOrWhiteSpace(kv.Value) && ContainsChinese(kv.Key))
            .OrderByDescending(kv => kv.Key.Length)
            .ToArray();
        _compositeCache.Clear();

        try
        {
            var culture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
        }

        _revision++;
        OnPropertyChanged(nameof(Revision));
    }

    public string Translate(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key ?? string.Empty;
        }

        if (_translations.TryGetValue(key, out var translation) && !string.IsNullOrWhiteSpace(translation))
        {
            return translation;
        }

        if (_translations.TryGetValue($"'{key}'", out var quotedTranslation) && !string.IsNullOrWhiteSpace(quotedTranslation))
        {
            return quotedTranslation;
        }

        var trimmed = key.Trim();
        if (trimmed.Length != key.Length
            && _translations.TryGetValue(trimmed, out var trimmedTranslation)
            && !string.IsNullOrWhiteSpace(trimmedTranslation))
        {
            return key.Replace(trimmed, trimmedTranslation, StringComparison.Ordinal);
        }

        if (!ContainsChinese(key))
        {
            return key;
        }

        return _compositeCache.GetOrAdd(key, TranslateComposite);
    }

    private string TranslateComposite(string input)
    {
        var result = input;
        foreach (var kv in _sortedPhrasePairs)
        {
            if (result.Contains(kv.Key, StringComparison.Ordinal))
            {
                result = result.Replace(kv.Key, kv.Value, StringComparison.Ordinal);
                if (!ContainsChinese(result))
                {
                    break;
                }
            }
        }

        result = result
            .Replace("：", ": ", StringComparison.Ordinal)
            .Replace("（", " (", StringComparison.Ordinal)
            .Replace("）", ")", StringComparison.Ordinal)
            .Replace("，", ", ", StringComparison.Ordinal)
            .Replace("。", ". ", StringComparison.Ordinal)
            .Replace("；", "; ", StringComparison.Ordinal)
            .Replace("、", ", ", StringComparison.Ordinal)
            .Replace("！", "!", StringComparison.Ordinal)
            .Replace("？", "?", StringComparison.Ordinal)
            .Replace("「", "\"", StringComparison.Ordinal)
            .Replace("」", "\"", StringComparison.Ordinal)
            .Replace("“", "\"", StringComparison.Ordinal)
            .Replace("”", "\"", StringComparison.Ordinal);

        return result.TrimEnd();
    }

    private static bool ContainsChinese(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff')
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyDictionary<string, string> LoadTranslations(string language)
    {
        try
        {
            var filePath = Path.Combine(_i18nDirectory, "en.json");
            if (!File.Exists(filePath))
            {
                return new Dictionary<string, string>();
            }

            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var dictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            return dictionary == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(dictionary, StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to load en.json: {exception}");
            return new Dictionary<string, string>();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
