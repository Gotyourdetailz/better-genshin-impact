using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.I18n;

/// <summary>
/// Translates BetterGI AutoPathing route paths, filenames, and info metadata
/// into clean, Windows-safe English equivalents while preserving executable route geometry.
/// </summary>
public static partial class PathingNameTranslator
{
    private const int MaxSegmentBaseLength = 90;

    private static readonly HashSet<string> UntouchedSystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "icon.ico",
        "README.md",
        "readme.md",
        "control.json5",
        "manifest.json",
        "settings.json"
    };

    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex MultiHyphenRegex = new(@"-{2,}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> KnownFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".txt", ".md", ".json5", ".xlsx", ".csv", ".tsv",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico", ".svg"
    };

    public static string TranslatePathSegment(string segment, bool isFile = false)
    {
        if (string.IsNullOrWhiteSpace(segment) || UntouchedSystemFiles.Contains(segment))
        {
            return segment;
        }

        var (rawStem, ext) = SplitTranslatableStem(segment, isFile);
        var translated = I18nService.Instance.Translate(rawStem);
        return FinalizeSegment(segment, translated, ext);
    }

    public static async Task<string> TranslatePathSegmentAsync(
        string segment,
        bool isFile = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(segment) || UntouchedSystemFiles.Contains(segment))
        {
            return segment;
        }

        var (rawStem, ext) = SplitTranslatableStem(segment, isFile);
        var translated = await I18nService.Instance.TranslateAsync(rawStem, cancellationToken).ConfigureAwait(false);
        return FinalizeSegment(segment, translated, ext);
    }

    private static (string Stem, string? Extension) SplitTranslatableStem(string segment, bool isFile)
    {
        var ext = Path.GetExtension(segment);
        if (!string.IsNullOrEmpty(ext) && (isFile || KnownFileExtensions.Contains(ext)))
        {
            return (Path.GetFileNameWithoutExtension(segment), ext);
        }

        return (segment, null);
    }

    private static string FinalizeSegment(string originalSegment, string translatedStem, string? ext)
    {
        var sanitized = SanitizePathSegment(translatedStem, MaxSegmentBaseLength);
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "Route_" + ShortHash(originalSegment);
        }

        return ext != null ? sanitized + ext : sanitized;
    }

    public static string TranslateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return relativePath;
        }

        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var translatedParts = parts.Select(p => TranslatePathSegment(p)).ToArray();
        return string.Join(Path.DirectorySeparatorChar, translatedParts);
    }

    public static async Task<string> TranslateRelativePathAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return relativePath;
        }

        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var translatedParts = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            translatedParts[i] = await TranslatePathSegmentAsync(parts[i], false, cancellationToken).ConfigureAwait(false);
        }

        return string.Join(Path.DirectorySeparatorChar, translatedParts);
    }

    public static string TranslatePathingJsonContent(string json)
    {
        return TranslatePathingJsonContentInternal(json, null);
    }

    public static async Task<string> TranslatePathingJsonContentAsync(
        string json,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is not JsonObject obj || obj["info"] is not JsonObject infoObj)
            {
                return json;
            }

            var changed = false;

            if (infoObj["name"]?.GetValue<string>() is { } rawName && I18nService.ContainsNonEnglishCjk(rawName))
            {
                var translatedName = await I18nService.Instance.TranslateAsync(rawName, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translatedName) && !string.Equals(translatedName, rawName, StringComparison.Ordinal))
                {
                    infoObj["name"] = translatedName;
                    changed = true;
                }
            }

            if (infoObj["description"]?.GetValue<string>() is { } rawDesc && I18nService.ContainsNonEnglishCjk(rawDesc))
            {
                var translatedDesc = await I18nService.Instance.TranslateAsync(rawDesc, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translatedDesc) && !string.Equals(translatedDesc, rawDesc, StringComparison.Ordinal))
                {
                    infoObj["description"] = translatedDesc;
                    changed = true;
                }
            }

            return changed ? obj.ToJsonString(IndentedJsonOptions) : json;
        }
        catch
        {
            return json;
        }
    }

    private static string TranslatePathingJsonContentInternal(string json, Func<string, string>? customTranslator)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is not JsonObject obj || obj["info"] is not JsonObject infoObj)
            {
                return json;
            }

            var translate = customTranslator ?? I18nService.Instance.Translate;
            var changed = false;

            if (infoObj["name"]?.GetValue<string>() is { } rawName && I18nService.ContainsNonEnglishCjk(rawName))
            {
                var translatedName = translate(rawName);
                if (!string.IsNullOrWhiteSpace(translatedName) && !string.Equals(translatedName, rawName, StringComparison.Ordinal))
                {
                    infoObj["name"] = translatedName;
                    changed = true;
                }
            }

            if (infoObj["description"]?.GetValue<string>() is { } rawDesc && I18nService.ContainsNonEnglishCjk(rawDesc))
            {
                var translatedDesc = translate(rawDesc);
                if (!string.IsNullOrWhiteSpace(translatedDesc) && !string.Equals(translatedDesc, rawDesc, StringComparison.Ordinal))
                {
                    infoObj["description"] = translatedDesc;
                    changed = true;
                }
            }

            return changed ? obj.ToJsonString(IndentedJsonOptions) : json;
        }
        catch
        {
            return json;
        }
    }

    /// <summary>
    /// Maps either a canonical Chinese material folder name or its English translated folder name
    /// back to the canonical ConditionDefinitions key.
    /// </summary>
    public static string? ResolveMaterialCanonicalKey(string segment, IEnumerable<string> canonicalMaterials)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return null;
        }

        foreach (var material in canonicalMaterials)
        {
            if (string.Equals(material, "全部", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(material, segment, StringComparison.OrdinalIgnoreCase))
            {
                return material;
            }

            var translated = SanitizePathSegment(I18nService.Instance.Translate(material), MaxSegmentBaseLength);
            if (string.Equals(translated, segment, StringComparison.OrdinalIgnoreCase))
            {
                return material;
            }
        }

        return null;
    }

    public static string SanitizePathSegment(string input, int maxLength = MaxSegmentBaseLength)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (invalid.Contains(ch) || char.IsControl(ch))
            {
                sb.Append(' ');
            }
            else if (ch is '【' or '「' or '“')
            {
                sb.Append('[');
            }
            else if (ch is '】' or '」' or '”')
            {
                sb.Append(']');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var cleaned = MultiSpaceRegex.Replace(sb.ToString(), " ");
        cleaned = MultiHyphenRegex.Replace(cleaned, "-");
        cleaned = cleaned.Trim(' ', '.', '-', '_');

        if (cleaned.Length > maxLength)
        {
            var suffix = "_" + ShortHash(input);
            var cutLength = Math.Max(1, maxLength - suffix.Length);
            cleaned = cleaned[..cutLength].TrimEnd(' ', '.', '-', '_') + suffix;
        }

        return cleaned;
    }

    private static string ShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes.AsSpan(0, 3)).ToLowerInvariant();
    }
}
