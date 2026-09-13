using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Srui.Generators;

/// <summary>Implements [Field] properties on Element subclasses
/// (docs/architecture.md section 4.2). A partial [Field] property gets
/// its implementation: a backing store, a read through the field's
/// binding when one is installed, a write through the binding's setter
/// (or into the store), and the written-field hook. Every [Field]
/// property, partial or hand-written, is registered: the type's
/// DescribeFields override writes it into the field set, and its
/// TryGet/TrySet overrides answer for it by key. The key is the
/// Srui.Fields member of the same name when one exists and the
/// property's type converts to it; otherwise a key named
/// <c>{Property}Field</c> is declared on the type.</summary>
[Generator]
public sealed class FieldGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Srui.FieldAttribute";
    private const string FieldsTypeName = "Srui.Fields";
    private const string ElementTypeName = "Srui.Element";

    private sealed record PropertyModel(
        string Name,
        string TypeFq,
        string Accessibility,
        bool IsPartial,
        bool HasSetter,
        string? SetterAccessibility,
        string KeyExpression,
        bool DeclaresKey,
        string? Diagnostic);

    /// <summary>A type with [Field] properties. Containers lists the
    /// enclosing types' headers, outermost first, for a nested type;
    /// every one of them must be partial too.</summary>
    private sealed record TypeModel(
        string? Namespace,
        string TypeHeader,
        string TypeName,
        string TypeFq,
        bool TypeIsPartial,
        ImmutableArray<string> Containers,
        bool DerivesElement,
        ImmutableArray<PropertyModel> Properties);

    private static readonly DiagnosticDescriptor PropertyNotPartial = new(
        "SRUIG001", "[Field] property with an implementation must not be partial",
        "'{0}.{1}' is declared partial but has an accessor body; drop partial to register it, or drop the body to have it implemented",
        "Srui.Fields", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor TypeNotPartial = new(
        "SRUIG002", "Type with [Field] properties must be partial",
        "'{0}' declares [Field] properties and must be declared partial (as must every type it is nested in)",
        "Srui.Fields", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor NotAnElement = new(
        "SRUIG004", "[Field] properties belong on Element subclasses",
        "'{0}' declares [Field] properties but does not derive from Srui.Element",
        "Srui.Fields", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor PartialNeedsSetter = new(
        "SRUIG005", "A partial [Field] property needs a setter",
        "'{0}.{1}' is a partial [Field] property and must declare both get and set; a computed field is an ordinary property with [Field]",
        "Srui.Fields", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor KeyTypeMismatch = new(
        "SRUIG006", "[Field] property type does not match the core key",
        "'{0}.{1}' is typed {2} but Srui.Fields.{1} is a Field<{3}>; use a convertible type or rename the property to declare its own key",
        "Srui.Fields", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var properties = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            predicate: static (node, _) => node is PropertyDeclarationSyntax,
            transform: static (ctx, _) => Extract(ctx));

        var byType = properties.Collect();

        context.RegisterSourceOutput(byType, static (spc, all) => Emit(spc, all));
    }

    private static (TypeModel Type, PropertyModel Property) Extract(GeneratorAttributeSyntaxContext ctx)
    {
        var format = SymbolDisplayFormat.FullyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        var prop = (IPropertySymbol)ctx.TargetSymbol;
        var syntax = (PropertyDeclarationSyntax)ctx.TargetNode;
        var type = prop.ContainingType;
        var compilation = ctx.SemanticModel.Compilation;

        bool typeIsPartial = IsPartial(type);
        var containers = ImmutableArray.CreateBuilder<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            if (!IsPartial(outer))
                typeIsPartial = false;
            containers.Insert(0, HeaderOf(outer));
        }

        bool derivesElement = false;
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == ElementTypeName) { derivesElement = true; break; }

        bool isPartial = syntax.Modifiers.Any(SyntaxKind.PartialKeyword);
        // A partial property whose declaration carries a body is the
        // implementing half of someone else's split; only a bodiless
        // declaration asks to be implemented here.
        bool hasBody = syntax.AccessorList?.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null) == true
            || syntax.ExpressionBody is not null;

        string propTypeFq = prop.Type.ToDisplayString(format);
        string? diagnostic = null;
        string keyExpression;
        bool declaresKey = false;

        var fieldsType = compilation.GetTypeByMetadataName(FieldsTypeName);
        var core = fieldsType?.GetMembers(prop.Name).OfType<IFieldSymbol>().FirstOrDefault(f => f.IsStatic);
        if (core is not null && core.Type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } keyType)
        {
            var keyArg = keyType.TypeArguments[0];
            var conversion = compilation.ClassifyConversion(prop.Type, keyArg);
            if (conversion.IsImplicit && !conversion.IsUserDefined)
                keyExpression = $"global::Srui.Fields.{prop.Name}";
            else
            {
                keyExpression = "";
                diagnostic = $"mismatch|{propTypeFq}|{keyArg.ToDisplayString(format)}";
            }
        }
        else
        {
            keyExpression = $"{prop.Name}Field";
            declaresKey = true;
        }

        var typeModel = new TypeModel(
            Namespace: type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            TypeHeader: HeaderOf(type),
            TypeName: type.Name,
            TypeFq: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TypeIsPartial: typeIsPartial,
            Containers: containers.ToImmutable(),
            DerivesElement: derivesElement,
            Properties: ImmutableArray<PropertyModel>.Empty);

        var propModel = new PropertyModel(
            Name: prop.Name,
            TypeFq: propTypeFq,
            Accessibility: SyntaxFacts.GetText(prop.DeclaredAccessibility),
            IsPartial: isPartial && !hasBody,
            HasSetter: prop.SetMethod is not null,
            SetterAccessibility: prop.SetMethod is { } set && set.DeclaredAccessibility != prop.DeclaredAccessibility
                ? SyntaxFacts.GetText(set.DeclaredAccessibility)
                : null,
            KeyExpression: keyExpression,
            DeclaresKey: declaresKey,
            Diagnostic: isPartial && hasBody ? "partial-with-body" : diagnostic);

        return (typeModel, propModel);
    }

    private static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var decl in type.DeclaringSyntaxReferences)
            if (decl.GetSyntax() is TypeDeclarationSyntax tds && !tds.Modifiers.Any(SyntaxKind.PartialKeyword))
                return false;
        return true;
    }

    /// <summary>The type's declaration header: its keyword and name with
    /// type parameters, no constraints (a partial part may omit them).</summary>
    private static string HeaderOf(INamedTypeSymbol type)
    {
        var keyword = type.TypeKind switch
        {
            TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
            _ => type.IsRecord ? "record" : "class",
        };
        var name = type.ToDisplayString(new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters));
        return $"{keyword} {name}";
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<(TypeModel Type, PropertyModel Property)> all)
    {
        foreach (var group in all.GroupBy(p => p.Type.TypeFq).OrderBy(g => g.Key, System.StringComparer.Ordinal))
        {
            var type = group.First().Type;
            var props = group.Select(p => p.Property).ToList();

            if (!type.TypeIsPartial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(TypeNotPartial, Location.None, type.TypeName));
                continue;
            }
            if (!type.DerivesElement)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotAnElement, Location.None, type.TypeName));
                continue;
            }
            bool bad = false;
            foreach (var p in props)
            {
                if (p.Diagnostic == "partial-with-body")
                {
                    spc.ReportDiagnostic(Diagnostic.Create(PropertyNotPartial, Location.None, type.TypeName, p.Name));
                    bad = true;
                }
                else if (p.Diagnostic is { } d && d.StartsWith("mismatch|"))
                {
                    var parts = d.Split('|');
                    spc.ReportDiagnostic(Diagnostic.Create(KeyTypeMismatch, Location.None, type.TypeName, p.Name, parts[1], parts[2]));
                    bad = true;
                }
                else if (p.IsPartial && !p.HasSetter)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(PartialNeedsSetter, Location.None, type.TypeName, p.Name));
                    bad = true;
                }
            }
            if (bad)
                continue;

            spc.AddSource(
                (type.Namespace is null ? "" : type.Namespace + ".") + type.TypeName + ".Fields.g.cs",
                SourceText.From(Render(type, props), Encoding.UTF8));
        }
    }

    private static string Render(TypeModel type, List<PropertyModel> props)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS8600, CS8601, CS8603, CS8604, CS8618, CS8625, CS8601, CS8602");
        sb.AppendLine();
        if (type.Namespace is not null)
        {
            sb.AppendLine($"namespace {type.Namespace};");
            sb.AppendLine();
        }
        foreach (var container in type.Containers)
        {
            sb.AppendLine($"partial {container}");
            sb.AppendLine("{");
        }
        sb.AppendLine($"partial {type.TypeHeader}");
        sb.AppendLine("{");

        foreach (var p in props)
        {
            if (p.DeclaresKey)
            {
                sb.AppendLine($"    /// <summary>The field key of <see cref=\"{p.Name}\"/> — for Bind, Suppress, Reread, and reader rendering tables.</summary>");
                sb.AppendLine($"    public static readonly global::Srui.Field<{p.TypeFq}> {p.Name}Field = new({SymbolDisplay.FormatLiteral(p.Name, quote: true)});");
                sb.AppendLine();
            }
        }

        foreach (var p in props)
        {
            if (!p.IsPartial)
                continue;
            string store = "__" + char.ToLowerInvariant(p.Name[0]) + p.Name.Substring(1);
            string setAccess = p.SetterAccessibility is { } sa ? sa + " " : "";
            sb.AppendLine($"    private {p.TypeFq} {store} = default!;");
            sb.AppendLine();
            sb.AppendLine($"    {p.Accessibility} partial {p.TypeFq} {p.Name}");
            sb.AppendLine("    {");
            // The binding is typed by the key, which may be wider than
            // the property (bool against Field<bool?>); the read casts
            // back.
            sb.AppendLine($"        get => TryGetBinding({p.KeyExpression}, out var __b) ? ({p.TypeFq})__b.Get()! : {store};");
            sb.AppendLine($"        {setAccess}set");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (TryGetBinding({p.KeyExpression}, out var __b)) __b.Set(value);");
            sb.AppendLine($"            else {store} = value;");
            sb.AppendLine($"            OnFieldWritten({p.KeyExpression});");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("    /// <inheritdoc/>");
        sb.AppendLine("    public override void DescribeFields(global::Srui.FieldSet s)");
        sb.AppendLine("    {");
        sb.AppendLine("        base.DescribeFields(s);");
        foreach (var p in props)
            sb.AppendLine($"        s.Set({p.KeyExpression}, this.{p.Name});");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    /// <inheritdoc/>");
        sb.AppendLine("    public override bool TryGet<__F>(global::Srui.Field<__F> field, out __F value)");
        sb.AppendLine("    {");
        foreach (var p in props)
        {
            sb.AppendLine($"        if (ReferenceEquals(field, {p.KeyExpression}))");
            sb.AppendLine("        {");
            sb.AppendLine($"            value = (__F)(object?)this.{p.Name}!;");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return base.TryGet(field, out value);");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    /// <inheritdoc/>");
        sb.AppendLine("    public override bool TrySet<__F>(global::Srui.Field<__F> field, __F value)");
        sb.AppendLine("    {");
        foreach (var p in props)
        {
            if (!p.HasSetter)
                continue;
            sb.AppendLine($"        if (ReferenceEquals(field, {p.KeyExpression}))");
            sb.AppendLine("        {");
            sb.AppendLine($"            this.{p.Name} = ({p.TypeFq})(object?)value!;");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return base.TrySet(field, value);");
        sb.AppendLine("    }");

        sb.AppendLine("}");
        foreach (var _ in type.Containers)
            sb.AppendLine("}");
        return sb.ToString();
    }
}
