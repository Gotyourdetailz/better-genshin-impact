using BetterGenshinImpact.Core.Config;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.I18n;

/// <summary>
/// Thread-safe persistent auto-translation cache with optional background web fallback
/// for any unseen non-English CJK text entering the application or downloaded repos.
/// </summary>
public sealed class AutoTranslationService : IDisposable
{
    private const int MaxCacheEntries = 50000;
    private const int MaxQueueSize = 512;
    private const int CircuitBreakerFailureThreshold = 3;
    private static readonly TimeSpan CircuitBreakerCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RevisionNotifyDebounce = TimeSpan.FromMilliseconds(400);

    private readonly string _cacheFilePath;
    private readonly Func<string, CancellationToken, Task<string?>> _translatorFunc;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueueSize)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly object _saveLock = new();
    private readonly HttpClient? _ownedHttpClient;
    private readonly Task _workerTask;

    private int _savePending;
    private int _notifyPending;
    private int _disposed;
    private int _consecutiveFailures;
    private long _circuitOpenUntilUtcTicks;

    public AutoTranslationService()
        : this(Global.Absolute(Path.Combine("User", "I18n", "auto_en_cache.json")), null)
    {
    }

    internal AutoTranslationService(string cacheFilePath, Func<string, CancellationToken, Task<string?>>? customTranslator)
    {
        _cacheFilePath = cacheFilePath;
        if (customTranslator != null)
        {
            _translatorFunc = customTranslator;
        }
        else
        {
            _ownedHttpClient = new HttpClient { Timeout = RequestTimeout };
            _translatorFunc = TranslateViaGoogleEndpointAsync;
        }

        LoadCacheFromDisk();
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler? TranslationCached;

    public static bool ContainsNonEnglishCjk(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            // CJK Unified Ideographs + Extension A + Hiragana + Katakana + Hangul
            if (ch is (>= '\u4e00' and <= '\u9fff')
                or (>= '\u3400' and <= '\u4dbf')
                or (>= '\u3040' and <= '\u30ff')
                or (>= '\uac00' and <= '\ud7af'))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryGetCached(string key, out string translated)
    {
        if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            translated = value;
            return true;
        }

        translated = string.Empty;
        return false;
    }

    public void EnqueueIfNeeded(string originalText)
    {
        if (Volatile.Read(ref _disposed) != 0
            || string.IsNullOrWhiteSpace(originalText)
            || originalText.Length > 500
            || !ContainsNonEnglishCjk(originalText)
            || _cache.ContainsKey(originalText)
            || _cache.Count >= MaxCacheEntries
            || IsCircuitOpen())
        {
            return;
        }

        if (_inFlight.TryAdd(originalText, 0))
        {
            if (!_queue.Writer.TryWrite(originalText))
            {
                _inFlight.TryRemove(originalText, out _);
            }
        }
    }

    public async Task<string?> TranslateAsync(string originalText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originalText) || !ContainsNonEnglishCjk(originalText))
        {
            return originalText;
        }

        if (originalText.Length > 500)
        {
            return null;
        }

        if (TryGetCached(originalText, out var cached))
        {
            return cached;
        }

        if (Volatile.Read(ref _disposed) != 0 || IsCircuitOpen())
        {
            return null;
        }

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            var translated = await _translatorFunc(originalText, linkedCts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(translated) && !string.Equals(translated, originalText, StringComparison.Ordinal))
            {
                ResetFailures();
                var cleaned = translated.Trim();
                StoreTranslation(originalText, cleaned);
                return cleaned;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure();
            Debug.WriteLine($"AutoTranslationService error: {ex.Message}");
        }

        return null;
    }

    public void StoreTranslation(string originalText, string translatedText)
    {
        if (string.IsNullOrWhiteSpace(originalText)
            || string.IsNullOrWhiteSpace(translatedText)
            || (_cache.Count >= MaxCacheEntries && !_cache.ContainsKey(originalText)))
        {
            return;
        }

        _cache[originalText] = translatedText;
        ScheduleSave();
        ScheduleNotify();
    }

    public void FlushCache()
    {
        SaveCacheToDisk();
    }

    private bool IsCircuitOpen()
    {
        var openUntil = Interlocked.Read(ref _circuitOpenUntilUtcTicks);
        return openUntil > DateTime.UtcNow.Ticks;
    }

    private void RecordFailure()
    {
        if (Interlocked.Increment(ref _consecutiveFailures) >= CircuitBreakerFailureThreshold)
        {
            Interlocked.Exchange(ref _circuitOpenUntilUtcTicks, DateTime.UtcNow.Add(CircuitBreakerCooldown).Ticks);
        }
    }

    private void ResetFailures()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _circuitOpenUntilUtcTicks, 0);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    if (_cache.ContainsKey(item) || IsCircuitOpen())
                    {
                        continue;
                    }

                    var translated = await _translatorFunc(item, _cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(translated) && !string.Equals(translated, item, StringComparison.Ordinal))
                    {
                        ResetFailures();
                        StoreTranslation(item, translated.Trim());
                    }

                    // Polite pacing to avoid hammering free endpoint
                    await Task.Delay(120, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                }
                catch
                {
                    RecordFailure();
                    // Silent fallback offline; UI already shows best glossary output
                }
                finally
                {
                    _inFlight.TryRemove(item, out _);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task<string?> TranslateViaGoogleEndpointAsync(string text, CancellationToken cancellationToken)
    {
        if (_ownedHttpClient == null)
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(text);
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q={encoded}";
        using var response = await _ownedHttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var segments = doc.RootElement[0];
        if (segments.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var seg in segments.EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
            {
                sb.Append(seg[0].GetString());
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private void ScheduleSave()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _savePending, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDebounceDelay, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _savePending, 0);
            }

            SaveCacheToDisk();
        });
    }

    private void ScheduleNotify()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _notifyPending, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RevisionNotifyDebounce, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _notifyPending, 0);
            }

            if (Volatile.Read(ref _disposed) == 0)
            {
                TranslationCached?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void LoadCacheFromDisk()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_cacheFilePath, Encoding.UTF8);
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (loaded == null)
            {
                return;
            }

            foreach (var kv in loaded)
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    _cache[kv.Key] = kv.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load auto_en_cache.json: {ex.Message}");
        }
    }

    private void SaveCacheToDisk()
    {
        lock (_saveLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var snapshot = new SortedDictionary<string, string>(_cache, StringComparer.Ordinal);
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                var tmpPath = $"{_cacheFilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
                File.Move(tmpPath, _cacheFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save auto_en_cache.json: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
        }

        SaveCacheToDisk();
        _cts.Dispose();
        _ownedHttpClient?.Dispose();
    }
}
