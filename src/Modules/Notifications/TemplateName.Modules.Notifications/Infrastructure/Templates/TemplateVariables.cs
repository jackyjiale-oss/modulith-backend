using Scriban;
using Scriban.Syntax;

namespace TemplateName.Modules.Notifications.Infrastructure.Templates;

/// <summary>
/// The variable names templates use, read from the parsed Scriban syntax tree. The renderer checks them before rendering, so a missing
/// variable fails with its name; the completeness tests compare them across cultures and against the type's declaration.
/// </summary>
internal static class TemplateVariables
{
    /// <summary>The product name from <see cref="TemplateOptions"/>; every template may use it. It is reserved: a supplied variable of this name is ignored.</summary>
    public const string ProductName = "product_name";

    /// <summary>The rendered <c>html</c> part, inserted by an email layout as it is (already encoded).</summary>
    public const string Content = "content";

    /// <summary>The rendered subject, HTML-encoded, for an email layout's <c>&lt;title&gt;</c>.</summary>
    public const string Subject = "subject";

    /// <summary>
    /// Reported for Scriban's <c>this</c> (the whole model: <c>this.action_url</c>, <c>with this</c>, <c>for x in this</c>,
    /// <c>import this</c>). It reads every variable, secrets included, without naming one, so it is never allowed: the renderer refuses
    /// it even when a variable of that name is supplied, and the completeness tests report it.
    /// </summary>
    public const string This = "this";

    /// <summary>The only variables an email layout sees.</summary>
    public static IReadOnlySet<string> LayoutVariables { get; } = new HashSet<string>([Content, Subject, ProductName], StringComparer.Ordinal);

    // Scriban's loop objects (for.index, while.index, tablerow.col) are provided by the engine, not by the model.
    private static readonly string[] EngineVariables = ["for", "while", "tablerow"];

    /// <summary>
    /// Every global name <paramref name="template"/> mentions as a variable, read or written: a name it assigns, captures, loops over or
    /// declares as a function or parameter counts too, because Scriban resolves such a name in the model, so a definition that never
    /// runs (<c>{{ if false }}{{ action_url = 'x' }}{{ end }}{{ action_url }}</c>) must not hide a read. A template's own names are
    /// <c>$</c> locals (<c>for $item in ...</c>, <c>$total = ...</c>) and are left out, as are member names (<c>a.b</c> mentions
    /// <c>a</c>), named-argument and object-member names. <c>this</c> is reported as <see cref="This"/>.
    /// </summary>
    public static IReadOnlySet<string> Collect(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var collector = new Collector();
        collector.Visit(template.Page);
        collector.Names.ExceptWith(EngineVariables);

        return collector.Names;
    }

    private sealed class Collector : ScriptVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

        public override void Visit(ScriptVariableGlobal node)
        {
            Names.Add(node.Name);
        }

        public override void Visit(ScriptThisExpression node)
        {
            Names.Add(This);
        }

        public override void Visit(ScriptMemberExpression node)
        {
            Visit(node.Target);
        }

        public override void Visit(ScriptNamedArgument node)
        {
            Visit(node.Value);
        }

        public override void Visit(ScriptObjectMember node)
        {
            Visit(node.Value);
        }
    }
}
