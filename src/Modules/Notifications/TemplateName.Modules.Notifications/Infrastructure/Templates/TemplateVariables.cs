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

    /// <summary>The only variables an email layout sees.</summary>
    public static IReadOnlySet<string> LayoutVariables { get; } = new HashSet<string>([Content, Subject, ProductName], StringComparer.Ordinal);

    // Scriban's loop objects (for.index, while.index, tablerow.col) are provided by the engine, not by the model.
    private static readonly string[] EngineVariables = ["for", "while", "tablerow"];

    /// <summary>
    /// The global names <paramref name="template"/> reads and does not define itself: names it assigns, captures, loops over or declares
    /// as a function or parameter are left out, as are member names (<c>a.b</c> reads <c>a</c>) and named-argument names.
    /// </summary>
    public static IReadOnlySet<string> Collect(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var collector = new Collector();
        collector.Visit(template.Page);
        collector.Read.ExceptWith(collector.Defined);
        collector.Read.ExceptWith(EngineVariables);

        return collector.Read;
    }

    private sealed class Collector : ScriptVisitor
    {
        public HashSet<string> Read { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Defined { get; } = new(StringComparer.Ordinal);

        public override void Visit(ScriptVariableGlobal node)
        {
            Read.Add(node.Name);
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

        public override void Visit(ScriptAssignExpression node)
        {
            Define(node.Target);
            Visit(node.Value);
        }

        public override void Visit(ScriptCaptureStatement node)
        {
            Define(node.Target);
            Visit(node.Body);
        }

        public override void Visit(ScriptForStatement node)
        {
            Define(node.Variable);
            Visit(node.Iterator);
            Visit(node.NamedArguments);
            Visit(node.Body);
            Visit(node.Else);
        }

        public override void Visit(ScriptTableRowStatement node)
        {
            Visit((ScriptForStatement)node);
        }

        public override void Visit(ScriptFunction node)
        {
            Define(node.NameOrDoToken as ScriptExpression);
            foreach (var parameter in node.Parameters ?? [])
            {
                Define(parameter.Name);
            }

            Visit(node.Body);
        }

        private void Define(ScriptExpression? target)
        {
            if (target is ScriptVariableGlobal variable)
            {
                Defined.Add(variable.Name);
            }
            else
            {
                // a.b = 1 or a[0] = 1 reads a.
                Visit(target);
            }
        }
    }
}
