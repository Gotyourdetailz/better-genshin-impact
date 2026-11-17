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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BetterGenshinImpact.Service.I18n;

/// <summary>
/// English-locked UI and runtime automatic translation service.
/// </summary>
public sealed partial class I18nService : INotifyPropertyChanged
{
    public const string DefaultLanguage = "en";

    private static readonly Regex RouteSummaryFullRegex = new(
        @"(?:路线信息：)?该路线预计用时\s*([\d.]+)\s*秒[，,]\s*包含以下怪物[：:]\s*(.+)",
        RegexOptions.Compiled);

    private static readonly Regex RouteSummaryCountRegex = new(
        @"(?:路线信息[：:])?\s*该路线预计用时\s*([\d.]+)\s*秒[，,]\s*包含\s*(?:(\d+)\s*只精英(?:\s*[与和]\s*(\d+)\s*只小怪)?|(\d+)\s*只小怪)(?:[，,]\s*预计收入\s*([\d.]+)\s*摩拉)?(?:[，,]\s*包含以下怪物[：:]\s*(.+))?",
        RegexOptions.Compiled);

    private static readonly Regex MobSummaryPrefixRegex = new(
        @"^(\d+)\s*丘丘暴徒\s+(\d+)\s*浊水幻灵\s+",
        RegexOptions.Compiled);

    private static readonly Regex EstTimePerItemRegex = new(
        @"约\s*([\d.]+)\s*秒\s*/\s*(\d+)\s*个",
        RegexOptions.Compiled);

    private static readonly Regex EstSecondsRegex = new(
        @"预计用时\s*([\d.]+)\s*秒",
        RegexOptions.Compiled);

    private static readonly Regex EstMoraRegex = new(
        @"预计收入\s*([\d.]+)\s*摩拉",
        RegexOptions.Compiled);

    private static readonly Regex UnitCountZhiRegex = new(
        @"(\d+)\s*只",
        RegexOptions.Compiled);

    private static readonly Regex UnitCountGeRegex = new(
        @"(\d+(?:~\d+)?)\s*个(?![\w\u4e00-\u9fff]*?(?:月|小时|分钟|秒|次|人|组))",
        RegexOptions.Compiled);

    private static readonly Regex DistanceMetersRegex = new(
        @"距离\s*(\d+(?:\.\d+)?)\s*m",
        RegexOptions.Compiled);

    private static readonly Regex MultipleSpacesRegex = new(
        @"[ \t]{2,}",
        RegexOptions.Compiled);

    private static readonly Regex DirectionSuffixRegex = new(
        @"-(东北|西北|东南|西南|上方|下方|左侧|右侧|中间|左上|右上|左下|右下|北|南|东|西|上|下|左|右)(?=[\s\-\d_.\(\[\)\]【】]|$)",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> DirectionSuffixMap = new(StringComparer.Ordinal)
    {
        ["东北"] = " NE",
        ["西北"] = " NW",
        ["东南"] = " SE",
        ["西南"] = " SW",
        ["上方"] = " Upper",
        ["下方"] = " Lower",
        ["左侧"] = " Left",
        ["右侧"] = " Right",
        ["中间"] = " Center",
        ["左上"] = " Top-Left",
        ["右上"] = " Top-Right",
        ["左下"] = " Bottom-Left",
        ["右下"] = " Bottom-Right",
        ["北"] = " N",
        ["南"] = " S",
        ["东"] = " E",
        ["西"] = " W",
        ["上"] = " Top",
        ["下"] = " Bottom",
        ["左"] = " Left",
        ["右"] = " Right",
    };

    private const int MaxCompositeCacheEntries = 10000;
    private readonly string _i18nDirectory = Global.Absolute(Path.Combine("User", "I18n"));
    private readonly AutoTranslationService _autoTranslationService = new();
    private IReadOnlyDictionary<string, string> _translations = new Dictionary<string, string>();
    private KeyValuePair<string, string>[] _sortedPhrasePairs = [];
    private readonly ConcurrentDictionary<string, string> _compositeCache = new(StringComparer.Ordinal);
    private bool _hasLoadedLanguage;
    private long _revision;

    private I18nService()
    {
        _autoTranslationService.TranslationCached += OnAutoTranslationCached;
        ChangeLanguage(DefaultLanguage);
    }

    public static I18nService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Binding notification revision counter for TExtension / TValueExtension.
    /// </summary>
    public long Revision => _revision;

    public void ChangeLanguage(string? language)
    {
        language = DefaultLanguage;
        if (!_hasLoadedLanguage)
        {
            _translations = LoadTranslations(language);
            _sortedPhrasePairs = _translations
                .Where(kv => kv.Key.Length >= 2 && !string.IsNullOrWhiteSpace(kv.Value) && ContainsChinese(kv.Key))
                .OrderByDescending(kv => kv.Key.Length)
                .ToArray();
            _compositeCache.Clear();
            _hasLoadedLanguage = true;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
        }

        RaiseRevisionChanged();
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

        if (_autoTranslationService.TryGetCached(key, out var autoCached))
        {
            return autoCached;
        }

        var trimmed = key.Trim();
        if (trimmed.Length != key.Length)
        {
            if (_translations.TryGetValue(trimmed, out var trimmedTranslation) && !string.IsNullOrWhiteSpace(trimmedTranslation))
            {
                return key.Replace(trimmed, trimmedTranslation, StringComparison.Ordinal);
            }

            if (_autoTranslationService.TryGetCached(trimmed, out var trimmedAutoCached))
            {
                return key.Replace(trimmed, trimmedAutoCached, StringComparison.Ordinal);
            }
        }

        if (!ContainsNonEnglishCjk(key))
        {
            return key;
        }

        if (_compositeCache.Count >= MaxCompositeCacheEntries && !_compositeCache.ContainsKey(key))
        {
            _compositeCache.Clear();
        }

        var result = _compositeCache.GetOrAdd(key, TranslateComposite);
        if (ContainsNonEnglishCjk(result))
        {
            _autoTranslationService.EnqueueIfNeeded(key);
        }

        return result;
    }

    public async Task<string> TranslateAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key ?? string.Empty;
        }

        var offline = Translate(key);
        if (!ContainsNonEnglishCjk(offline))
        {
            return offline;
        }

        var online = await _autoTranslationService.TranslateAsync(offline, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(online))
        {
            _autoTranslationService.StoreTranslation(key, online);
            _compositeCache[key] = online;
            return online;
        }

        return offline;
    }

    public static bool ContainsNonEnglishCjk(string? text)
    {
        return AutoTranslationService.ContainsNonEnglishCjk(text);
    }

    public void FlushAutoTranslationCache()
    {
        _autoTranslationService.FlushCache();
    }

    public void DisposeAutoTranslation()
    {
        _autoTranslationService.Dispose();
    }

    private string TranslateComposite(string input)
    {
        if (_autoTranslationService.TryGetCached(input, out var cachedExact))
        {
            return cachedExact;
        }

        var result = TranslateStructuredPatterns(input);

        if (ContainsNonEnglishCjk(result))
        {
            foreach (var kv in _sortedPhrasePairs)
            {
                if (result.Contains(kv.Key, StringComparison.Ordinal))
                {
                    result = ReplaceWithCleanSpacing(result, kv.Key, kv.Value);
                    if (!ContainsNonEnglishCjk(result))
                    {
                        break;
                    }
                }
            }
        }

        if (ContainsNonEnglishCjk(result))
        {
            result = DirectionSuffixRegex.Replace(result, m =>
                DirectionSuffixMap.TryGetValue(m.Groups[1].Value, out var mapped) ? mapped : m.Value);
        }

        result = NormalizePunctuationAndSpacing(result);

        if (ContainsNonEnglishCjk(result) && _autoTranslationService.TryGetCached(result, out var cachedTail))
        {
            return cachedTail;
        }

        return result;
    }

    private string TranslateStructuredPatterns(string input)
    {
        var result = input;

        result = MobSummaryPrefixRegex.Replace(result, m =>
            $"{m.Groups[1].Value}x Mitachurl {m.Groups[2].Value}x Hydro Phantasm | ");

        result = RouteSummaryCountRegex.Replace(result, m =>
        {
            var sb = new StringBuilder();
            sb.Append($"Est. {m.Groups[1].Value}s");
            if (m.Groups[2].Success && m.Groups[3].Success)
            {
                sb.Append($", {m.Groups[2].Value} elite(s) & {m.Groups[3].Value} mob(s)");
            }
            else if (m.Groups[2].Success)
            {
                sb.Append($", {m.Groups[2].Value} elite(s)");
            }
            else if (m.Groups[4].Success)
            {
                sb.Append($", {m.Groups[4].Value} mob(s)");
            }

            if (m.Groups[5].Success)
            {
                sb.Append($", est. {m.Groups[5].Value} Mora");
            }

            if (m.Groups[6].Success)
            {
                sb.Append($". Enemies: {TranslateEnemiesFragment(m.Groups[6].Value)}");
            }

            return sb.ToString();
        });

        result = RouteSummaryFullRegex.Replace(result, m =>
            $"Est. {m.Groups[1].Value}s. Enemies: {TranslateEnemiesFragment(m.Groups[2].Value)}");

        result = EstTimePerItemRegex.Replace(result, m =>
            $"~{m.Groups[1].Value}s / {m.Groups[2].Value}x");

        result = EstSecondsRegex.Replace(result, m => $"Est. {m.Groups[1].Value}s");
        result = EstMoraRegex.Replace(result, m => $"Est. {m.Groups[1].Value} Mora");
        result = DistanceMetersRegex.Replace(result, m => $"Distance {m.Groups[1].Value}m");
        result = UnitCountZhiRegex.Replace(result, m => $"{m.Groups[1].Value}x ");
        result = UnitCountGeRegex.Replace(result, m => m.Groups[1].Value);

        return result;
    }

    private string TranslateEnemiesFragment(string raw)
    {
        var current = raw;
        current = UnitCountZhiRegex.Replace(current, m => $"{m.Groups[1].Value}x ");
        foreach (var kv in _sortedPhrasePairs)
        {
            if (current.Contains(kv.Key, StringComparison.Ordinal))
            {
                current = ReplaceWithCleanSpacing(current, kv.Key, kv.Value);
            }
        }

        return NormalizePunctuationAndSpacing(current);
    }

    private static string ReplaceWithCleanSpacing(string source, string token, string replacement)
    {
        var sb = new StringBuilder(source.Length + replacement.Length);
        var index = 0;
        while (true)
        {
            var matchIndex = source.IndexOf(token, index, StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                sb.Append(source, index, source.Length - index);
                break;
            }

            sb.Append(source, index, matchIndex - index);

            if (sb.Length > 0 && replacement.Length > 0
                && NeedsWordSeparator(sb[^1], replacement[0]))
            {
                sb.Append(' ');
            }

            sb.Append(replacement);

            var nextIndex = matchIndex + token.Length;
            if (nextIndex < source.Length && replacement.Length > 0
                && NeedsWordSeparator(replacement[^1], source[nextIndex]))
            {
                sb.Append(' ');
            }

            index = nextIndex;
        }

        return sb.ToString();
    }

    private static bool NeedsWordSeparator(char left, char right)
    {
        var leftAlphaNum = char.IsLetterOrDigit(left);
        var rightAlphaNum = char.IsLetterOrDigit(right);
        if (!leftAlphaNum || !rightAlphaNum)
        {
            return false;
        }

        // Preserve numbers joined directly to prefixes/suffixes like A01 or x4
        if (char.IsDigit(left) && right == 'x')
        {
            return false;
        }

        // Separate Latin letter <-> non-ASCII or digit <-> Latin letter cleanly except version/id tokens
        return (IsAsciiLetter(left) && IsAsciiLetter(right))
               || (char.IsDigit(left) && IsAsciiLetter(right))
               || (IsAsciiLetter(left) && char.IsDigit(right))
               || (IsAsciiLetter(left) && right >= '\u0080')
               || (left >= '\u0080' && IsAsciiLetter(right));
    }

    private static bool IsAsciiLetter(char c)
    {
        return c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
    }

    private static string NormalizePunctuationAndSpacing(string input)
    {
        var result = input
            .Replace("：", ": ", StringComparison.Ordinal)
            .Replace("（", " (", StringComparison.Ordinal)
            .Replace("）", ")", StringComparison.Ordinal)
            .Replace("，", ", ", StringComparison.Ordinal)
            .Replace("。", ". ", StringComparison.Ordinal)
            .Replace("；", "; ", StringComparison.Ordinal)
            .Replace("、", ", ", StringComparison.Ordinal)
            .Replace("！", "! ", StringComparison.Ordinal)
            .Replace("？", "? ", StringComparison.Ordinal)
            .Replace("「", "\"", StringComparison.Ordinal)
            .Replace("」", "\"", StringComparison.Ordinal)
            .Replace("“", "\"", StringComparison.Ordinal)
            .Replace("”", "\"", StringComparison.Ordinal)
            .Replace("【", "[", StringComparison.Ordinal)
            .Replace("】", "]", StringComparison.Ordinal)
            .Replace("·", " - ", StringComparison.Ordinal);

        result = MultipleSpacesRegex.Replace(result, " ");
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

    private void OnAutoTranslationCached(object? sender, EventArgs e)
    {
        _compositeCache.Clear();
        RaiseRevisionChanged();
    }

    private void RaiseRevisionChanged()
    {
        Interlocked.Increment(ref _revision);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(Revision)));
        }
        else
        {
            OnPropertyChanged(nameof(Revision));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
