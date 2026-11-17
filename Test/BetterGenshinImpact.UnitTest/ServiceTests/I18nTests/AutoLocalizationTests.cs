using BetterGenshinImpact.Core.Script.Utils;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.I18n;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace BetterGenshinImpact.UnitTest.ServiceTests.I18nTests;

public class AutoLocalizationTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void I18nService_TranslateStructuredPatterns_ShouldTranslateRouteSummariesAndCounts()
    {
        var fullSummary = I18nService.Instance.Translate(
            "该路线预计用时53秒，包含4只精英，预计收入800摩拉，包含以下怪物：1只遗迹巡弋者");
        Assert.DoesNotContain("预计用时", fullSummary);
        Assert.DoesNotContain("精英", fullSummary);
        Assert.DoesNotContain("摩拉", fullSummary);
        Assert.Contains("Est. 53s", fullSummary);
        Assert.Contains("4 elite(s)", fullSummary);
        Assert.Contains("est. 800 Mora", fullSummary);

        var mixedSummary = I18nService.Instance.Translate(
            "该路线预计用时120秒，包含2只精英与5只小怪");
        Assert.Equal("Est. 120s, 2 elite(s) & 5 mob(s)", mixedSummary);

        var distance = I18nService.Instance.Translate("距离12.5m");
        Assert.Equal("Distance 12.5m", distance);

        var direction = I18nService.Instance.Translate("A01-Test-东北");
        Assert.Contains("A01-Test", direction);
        Assert.Contains("NE", direction);
    }

    [Theory]
    [InlineData("Hello World", false)]
    [InlineData("C46-Mint-Mondstadt 12x", false)]
    [InlineData("Café résumé", false)]
    [InlineData("点击黑屏", true)]
    [InlineData("こんにちは", true)]
    [InlineData("안녕하세요", true)]
    [InlineData("Mix English and 中文", true)]
    public void AutoTranslationService_ContainsNonEnglishCjk_ShouldDetectAllCjkScripts(string text, bool expected)
    {
        Assert.Equal(expected, AutoTranslationService.ContainsNonEnglishCjk(text));
    }

    [Fact]
    public async Task AutoTranslationService_TranslateAndPersist_ShouldSaveAndReloadCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bgi-i18n-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var cachePath = Path.Combine(tempDir, "auto_en_cache.json");

        try
        {
            using (var service = new AutoTranslationService(
                cachePath,
                (text, _) => Task.FromResult<string?>(text == "测试词条" ? "Test Entry" : null)))
            {
                var translated = await service.TranslateAsync("测试词条");
                Assert.Equal("Test Entry", translated);
                Assert.True(service.TryGetCached("测试词条", out var cached));
                Assert.Equal("Test Entry", cached);
                service.FlushCache();
            }

            Assert.True(File.Exists(cachePath));

            using var reloaded = new AutoTranslationService(
                cachePath,
                (_, _) => Task.FromResult<string?>("ShouldNotBeCalled"));
            Assert.True(reloaded.TryGetCached("测试词条", out var persisted));
            Assert.Equal("Test Entry", persisted);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void PathingNameTranslator_SanitizeAndJsonRewrite_ShouldPreservePositionsAndSanitizePath()
    {
        var sanitized = PathingNameTranslator.SanitizePathSegment("01: Test/Route*Name?\"<>|【北】");
        Assert.Equal("01 Test Route Name [北]", sanitized);

        const string sampleJson = """
        {
          "info": {
            "name": "该路线预计用时10秒，包含2只精英",
            "description": "距离25m",
            "type": "collect"
          },
          "positions": [
            {
              "x": 123.45,
              "y": 678.9,
              "action": "fight",
              "action_params": "nahida e"
            }
          ]
        }
        """;

        var rewritten = PathingNameTranslator.TranslatePathingJsonContent(sampleJson);
        Assert.Contains("Est. 10s, 2 elite(s)", rewritten);
        Assert.Contains("Distance 25m", rewritten);
        Assert.Contains("\"x\": 123.45", rewritten);
        Assert.Contains("\"action_params\": \"nahida e\"", rewritten);
    }

    [Fact]
    public void PathingNameTranslator_ResolveMaterialCanonicalKey_ShouldMatchChineseAndEnglish()
    {
        var materials = new[] { "全部", "劫波莲", "树王圣体菇", "琉璃袋" };

        Assert.Null(PathingNameTranslator.ResolveMaterialCanonicalKey("All", materials));
        Assert.Equal("劫波莲", PathingNameTranslator.ResolveMaterialCanonicalKey("劫波莲", materials));

        var englishFolder = PathingNameTranslator.TranslatePathSegment("劫波莲");
        if (!string.Equals(englishFolder, "劫波莲", StringComparison.Ordinal))
        {
            Assert.Equal("劫波莲", PathingNameTranslator.ResolveMaterialCanonicalKey(englishFolder, materials));
        }

        Assert.Null(PathingNameTranslator.ResolveMaterialCanonicalKey("NonExistentItem", materials));
    }

    [Fact]
    public void PathingNameTranslator_LongAssetFilename_ShouldPreserveExtension()
    {
        var longImageName = new string('a', 110) + "_中文图片.png";
        var translated = PathingNameTranslator.TranslatePathSegment(longImageName, isFile: true);
        Assert.EndsWith(".png", translated, StringComparison.Ordinal);
        Assert.True(Path.GetFileNameWithoutExtension(translated).Length <= 90);
    }

    [Fact]
    public void JsonMerger_MergeJson_ShouldMatchTranslatedRouteNameAgainstChineseControlEntry()
    {
        var translatedStem = PathingNameTranslator.TranslatePathSegment("测试路线-东北.json", isFile: true);
        var translatedName = Path.GetFileNameWithoutExtension(translatedStem);

        const string controlJson = """
        {
          "json_list": [
            {
              "name": "测试路线-东北",
              "cover": {
                "info": {
                  "type": "target"
                }
              }
            }
          ]
        }
        """;

        const string routeJson = """
        {
          "info": {
            "name": "Test Route",
            "type": "collect"
          }
        }
        """;

        var merged = JsonMerger.MergeJson(controlJson, routeJson, translatedName);
        Assert.Contains("\"type\": \"target\"", merged);
    }

    [Fact]
    public void TranslatingLogEventSink_ShouldTranslateCjkMessagesWithBracesAndPassThroughEnglish()
    {
        var inner = new CapturingSink();
        var parser = new MessageTemplateParser();
        TranslatingLogEventSink? sink = null;
        sink = new TranslatingLogEventSink(
            inner,
            text =>
            {
                // Re-entrant Emit call inside translator must not recurse infinitely
                sink!.Emit(new LogEvent(
                    DateTimeOffset.UtcNow,
                    LogEventLevel.Debug,
                    null,
                    parser.Parse("reentrant 中文"),
                    Array.Empty<LogEventProperty>()));

                if (text.Contains("中文异常", StringComparison.Ordinal))
                {
                    return "Translated Error";
                }

                return text.Contains("中文", StringComparison.Ordinal) ? "Translated {value}" : text;
            });

        using (sink)
        {
            var cjkEvent = new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                new InvalidOperationException("中文异常"),
                parser.Parse("前缀 {Item}"),
                new[] { new LogEventProperty("Item", new ScalarValue("中文")) });

            sink.Emit(cjkEvent);
            Assert.Equal(3, inner.Events.Count);
            Assert.Equal("reentrant 中文", inner.Events[0].RenderMessage());
            Assert.Equal("reentrant 中文", inner.Events[1].RenderMessage());
            Assert.Equal("Translated {value}", inner.Events[2].RenderMessage());
            Assert.Equal("Translated Error", inner.Events[2].Exception?.Message);

            var englishEvent = new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Warning,
                null,
                parser.Parse("Pure English log"),
                Array.Empty<LogEventProperty>());

            sink.Emit(englishEvent);
            Assert.Equal(4, inner.Events.Count);
            Assert.Equal("Pure English log", inner.Events[3].RenderMessage());
        }
    }
}
