using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Infrastructure.Templates;

/// <summary>
/// Renders notifications with Scriban (ADR 0019). Templates are trusted code reviewed in pull requests; variable values are untrusted
/// data, so a template sees nothing but strings:
/// <list type="bullet">
/// <item>Each part gets a fresh model holding the supplied variables and <c>product_name</c>, nothing else: no built-in functions
/// (no <c>include</c>, <c>date.now</c> or <c>object.eval</c>), no template loader, no .NET member of any value.</item>
/// <item>Values are data: they are never parsed as templates. For <c>html</c> parts every value is HTML-encoded before it enters the
/// model, so a template cannot forget; the layout receives the rendered body as <c>content</c> unencoded and the subject and product
/// name encoded. Text parts, subjects and in-app parts are plain text and get the values as they are.</item>
/// <item><see cref="TemplateContext.StrictVariables"/>, invariant culture, loops of at most 1,000 iterations, recursion of at most 20
/// calls and output of at most <see cref="MaxOutputLength"/> characters; a failure is a <see cref="TemplateRenderException"/> that
/// never quotes a value.</item>
/// </list>
/// Subjects and in-app titles are one line (line breaks and other control characters become spaces) of at most
/// <see cref="MaxSubjectLength"/> characters; every part is trimmed.
/// </summary>
internal sealed class ScribanNotificationRenderer(EmbeddedTemplateStore store, IOptions<TemplateOptions> options) : INotificationRenderer
{
    /// <summary>The longest subject or in-app title, in UTF-16 units (the <c>RenderedSubject</c> column).</summary>
    public const int MaxSubjectLength = 300;

    /// <summary>The most characters one part may write before rendering fails.</summary>
    public const int MaxOutputLength = 200_000;

    private const int LoopLimit = 1000;
    private const int RecursiveLimit = 20;

    public RenderedMessage Render(string typeCode, NotificationChannel channel, string culture, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(typeCode);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(variables);

        var request = new RenderRequest(typeCode, channel, culture, variables, options.Value.ProductName);

        return channel switch
        {
            NotificationChannel.Email => RenderEmail(request),
            NotificationChannel.InApp => new RenderedMessage(
                SingleLine(RenderPart(request, "title", isHtml: false)),
                RenderPart(request, "body", isHtml: false),
                null),
            _ => throw new TemplateRenderException(typeCode, channel, culture, "the channel has no templates"),
        };
    }

    private RenderedMessage RenderEmail(RenderRequest request)
    {
        var subject = SingleLine(RenderPart(request, "subject", isHtml: false));
        var text = RenderPart(request, "text", isHtml: false);
        var body = RenderPart(request, "html", isHtml: true);

        var layoutModel = new ScriptObject();
        layoutModel.SetValue(TemplateVariables.Content, body, true);
        layoutModel.SetValue(TemplateVariables.Subject, WebUtility.HtmlEncode(subject), true);
        layoutModel.SetValue(TemplateVariables.ProductName, WebUtility.HtmlEncode(request.ProductName), true);
        var html = Render(store.GetLayout(request.TypeCode, request.Culture), layoutModel, request);

        return new RenderedMessage(subject, text, html);
    }

    private string RenderPart(RenderRequest request, string part, bool isHtml)
    {
        var model = new ScriptObject();
        foreach (var (name, value) in request.Variables)
        {
            model.SetValue(name, isHtml ? WebUtility.HtmlEncode(value ?? string.Empty) : value ?? string.Empty, true);
        }

        // Reserved: the configured product name wins over a supplied variable of the same name.
        model.SetValue(TemplateVariables.ProductName, isHtml ? WebUtility.HtmlEncode(request.ProductName) : request.ProductName, true);

        return Render(store.GetPart(request.TypeCode, request.Channel, request.Culture, part), model, request);
    }

    private static string Render(StoredTemplate template, ScriptObject model, RenderRequest request)
    {
        // 'this' is the whole model (every secret included); it never counts as supplied, even if a variable has that name.
        var missing = template.Variables
            .Where(name => name == TemplateVariables.This || !model.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            throw new TemplateRenderException(
                request.TypeCode,
                request.Channel,
                request.Culture,
                $"{template.ResourceName} uses variables that were not supplied: {string.Join(", ", missing)}");
        }

        var context = CreateContext();
        context.PushGlobal(model);
        try
        {
            return template.Template.Render(context).Trim();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The engine's message can quote a value (a conversion error, for example), so neither it nor the exception goes along.
            var position = exception is ScriptRuntimeException runtimeException
                ? $"at line {runtimeException.Span.Start.Line + 1}, column {runtimeException.Span.Start.Column + 1} "
                : string.Empty;
            throw new TemplateRenderException(
                request.TypeCode,
                request.Channel,
                request.Culture,
                $"{template.ResourceName} failed while rendering {position}({exception.GetType().Name})");
        }
    }

    private static TemplateContext CreateContext()
    {
        // An empty builtin object: no functions at all, so nothing reaches the file system, the clock or the template engine itself.
        var context = new TemplateContext(new ScriptObject())
        {
            StrictVariables = true,
            LoopLimit = LoopLimit,
            RecursiveLimit = RecursiveLimit,
            MemberRenamer = member => member.Name,
            MemberFilter = _ => false,
            EnableRelaxedMemberAccess = false,
            TemplateLoader = null,
            RegexTimeOut = TimeSpan.FromSeconds(1),
            OutputLimit = MaxOutputLength,
            OnOutputLimit = ScriptLimitBehavior.Throw,
            NewLine = "\n",
        };
        context.PushCulture(CultureInfo.InvariantCulture);

        return context;
    }

    private static string SingleLine(string value)
    {
        var line = new StringBuilder(value.Length);
        var previousWasCarriageReturn = false;
        foreach (var character in value)
        {
            // CR LF is one break, so it becomes one space.
            if (character == '\n' && previousWasCarriageReturn)
            {
                previousWasCarriageReturn = false;
                continue;
            }

            previousWasCarriageReturn = character == '\r';
            line.Append(char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character);
        }

        return BoundedText.Cut(line.ToString().Trim(), MaxSubjectLength);
    }

    private sealed record RenderRequest(
        string TypeCode,
        NotificationChannel Channel,
        string Culture,
        IReadOnlyDictionary<string, string> Variables,
        string ProductName);
}
