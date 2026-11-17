using BetterGenshinImpact.Service.I18n;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace BetterGenshinImpact.Helpers;

/// <summary>
/// Intercepts Serilog log events and automatically translates any non-English CJK
/// text to English before forwarding to Console, File, and Overlay UI sinks.
/// </summary>
public sealed class TranslatingLogEventSink : ILogEventSink, IDisposable
{
    private static readonly MessageTemplateParser TemplateParser = new();

    [ThreadStatic]
    private static bool _isTranslating;

    private readonly ILogEventSink _innerSink;
    private readonly Func<string, string> _translator;

    public TranslatingLogEventSink(ILogEventSink innerSink, Func<string, string>? translator = null)
    {
        _innerSink = innerSink ?? throw new ArgumentNullException(nameof(innerSink));
        _translator = translator ?? I18nService.Instance.Translate;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return;
        }

        if (_isTranslating)
        {
            _innerSink.Emit(logEvent);
            return;
        }

        _isTranslating = true;
        try
        {
            var rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture);
            var hasCjkMessage = I18nService.ContainsNonEnglishCjk(rendered);
            var exceptionToEmit = TranslateExceptionIfNeeded(logEvent.Exception);

            if (!hasCjkMessage && ReferenceEquals(exceptionToEmit, logEvent.Exception))
            {
                _innerSink.Emit(logEvent);
                return;
            }

            var messageText = rendered;
            if (hasCjkMessage)
            {
                var translated = _translator(rendered);
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    messageText = translated;
                }
            }

            var escaped = messageText.Replace("{", "{{", StringComparison.Ordinal)
                                     .Replace("}", "}}", StringComparison.Ordinal);
            var parsedTemplate = TemplateParser.Parse(escaped);
            var translatedEvent = new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                exceptionToEmit,
                parsedTemplate,
                CopyProperties(logEvent.Properties));

            _innerSink.Emit(translatedEvent);
        }
        catch
        {
            _innerSink.Emit(logEvent);
        }
        finally
        {
            _isTranslating = false;
        }
    }

    private Exception? TranslateExceptionIfNeeded(Exception? exception)
    {
        if (exception is null || !I18nService.ContainsNonEnglishCjk(exception.Message))
        {
            return exception;
        }

        var translatedMessage = _translator(exception.Message);
        if (string.IsNullOrWhiteSpace(translatedMessage)
            || string.Equals(translatedMessage, exception.Message, StringComparison.Ordinal))
        {
            return exception;
        }

        return new TranslatedLogException(translatedMessage, exception);
    }

    private sealed class TranslatedLogException : Exception
    {
        private readonly Exception _original;

        public TranslatedLogException(string translatedMessage, Exception original)
            : base(translatedMessage, original)
        {
            _original = original;
        }

        public override string? StackTrace => _original.StackTrace;

        public override string ToString()
        {
            var stack = _original.StackTrace;
            return string.IsNullOrEmpty(stack)
                ? $"{_original.GetType().FullName}: {Message}"
                : $"{_original.GetType().FullName}: {Message}{Environment.NewLine}{stack}";
        }
    }

    private static IEnumerable<LogEventProperty> CopyProperties(IReadOnlyDictionary<string, LogEventPropertyValue> properties)
    {
        var list = new List<LogEventProperty>(properties.Count);
        foreach (var kv in properties)
        {
            list.Add(new LogEventProperty(kv.Key, kv.Value));
        }

        return list;
    }

    public void Dispose()
    {
        (_innerSink as IDisposable)?.Dispose();
    }
}
