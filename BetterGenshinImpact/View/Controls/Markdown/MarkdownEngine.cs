using BetterGenshinImpact.Service.I18n;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.EmphasisExtras;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.View.Controls.Markdown;

public interface IMarkdownEngine
{
    Task<MarkdownRenderPlan> CreatePlanAsync(
        MarkdownSource source,
        MarkdownOptions options,
        CancellationToken cancellationToken);

    MarkdownRenderResult Render(
        MarkdownRenderPlan plan,
        MarkdownView owner,
        IMarkdownImageLoader imageLoader,
        CancellationToken cancellationToken);
}

public sealed class MarkdownEngine : IMarkdownEngine
{
    public static MarkdownEngine Default { get; } = new();

    private readonly MarkdownPipeline _commonMarkPipeline;
    private readonly MarkdownPipeline _gfmPipeline;
    private readonly MarkdownPipeline _enhancedPipeline;

    public MarkdownEngine()
    {
        _commonMarkPipeline = new MarkdownPipelineBuilder().Build();

        _gfmPipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .UseAutoLinks()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UseCjkFriendlyEmphasis()
            .Build();

        _enhancedPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseEmojiAndSmiley()
            .UseYamlFrontMatter()
            .UseCjkFriendlyEmphasis()
            .UseAlertBlocks()
            .Build();
    }

    public async Task<MarkdownRenderPlan> CreatePlanAsync(
        MarkdownSource source,
        MarkdownOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveSource = source;
        if (I18nService.ContainsNonEnglishCjk(source.Text))
        {
            var translatedText = await TranslateMarkdownTextAsync(source.Text, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(translatedText, source.Text, StringComparison.Ordinal))
            {
                effectiveSource = source with { Text = translatedText };
            }
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipeline = options.Profile switch
            {
                MarkdownProfile.CommonMark => _commonMarkPipeline,
                MarkdownProfile.Gfm => _gfmPipeline,
                _ => _enhancedPipeline
            };

            var document = Markdig.Markdown.Parse(effectiveSource.Text, pipeline);
            cancellationToken.ThrowIfCancellationRequested();
            return new MarkdownRenderPlan(document, effectiveSource, options);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static readonly System.Text.RegularExpressions.Regex InlineTokenRegex = new(
        @"`[^`]+`|!?\[[^\]]*\]\([^)]+\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task<string> TranslateMarkdownTextAsync(string markdown, CancellationToken cancellationToken)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder(markdown.Length + 256);
        var inFence = false;
        var networkFailed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                sb.Append(line);
            }
            else if (!inFence && I18nService.ContainsNonEnglishCjk(line))
            {
                sb.Append(await TranslateMarkdownLineSafeAsync(line, () => networkFailed, () => networkFailed = true, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                sb.Append(line);
            }

            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static async Task<string> TranslateMarkdownLineSafeAsync(
        string line,
        Func<bool> isNetworkFailed,
        Action markNetworkFailed,
        CancellationToken cancellationToken)
    {
        // Preserve inline code spans and markdown URLs verbatim while translating surrounding text
        var tokens = new System.Collections.Generic.List<string>();
        var masked = InlineTokenRegex.Replace(line, m =>
        {
            var tokenIndex = tokens.Count;
            tokens.Add(m.Value);
            return $"__MD_TOKEN_{tokenIndex}__";
        });

        string translatedMasked;
        if (isNetworkFailed())
        {
            translatedMasked = I18nService.Instance.Translate(masked);
        }
        else
        {
            var attempt = await I18nService.Instance.TranslateAsync(masked, cancellationToken).ConfigureAwait(false);
            if (I18nService.ContainsNonEnglishCjk(attempt))
            {
                // Online endpoint returned CJK unchanged or failed; avoid retrying 4s timeouts for every subsequent line
                markNetworkFailed();
            }

            translatedMasked = attempt;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            translatedMasked = translatedMasked.Replace($"__MD_TOKEN_{i}__", tokens[i], StringComparison.Ordinal);
        }

        return translatedMasked;
    }

    public MarkdownRenderResult Render(
        MarkdownRenderPlan plan,
        MarkdownView owner,
        IMarkdownImageLoader imageLoader,
        CancellationToken cancellationToken)
    {
        owner.Dispatcher.VerifyAccess();
        return new MarkdownWpfRenderer(plan, owner, imageLoader, cancellationToken).Render();
    }
}
