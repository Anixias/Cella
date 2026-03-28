using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cella.SourceGenerators;

[Generator(LanguageNames.CSharp)]
public sealed class TreeVisitorGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat _typeNameFormat =
        new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions:
                SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions:
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly DiagnosticDescriptor _interfaceMustBePartial = new(
        id: "TVG001",
        title: "Tree visitor target must be partial",
        messageFormat: "Interface '{0}' must be declared partial to receive generated visitor members",
        category: "TreeVisitorGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource("TreeVisitorAttribute.g.cs", SourceText.From(
"""
#nullable enable

using System;

[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class TreeVisitorAttribute<TNode> : Attribute
{
}
""", Encoding.UTF8));
        });

        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is InterfaceDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, _) => GetTarget(ctx))
            .Where(static x => x is not null);

        var compilationAndTargets = context.CompilationProvider.Combine(candidates.Collect());

        context.RegisterSourceOutput(compilationAndTargets, static (spc, pair) =>
        {
            Execute(pair.Left, pair.Right, spc);
        });
    }

    private static VisitorTarget? GetTarget(GeneratorSyntaxContext context)
    {
        var interfaceSyntax = (InterfaceDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(interfaceSyntax) is not INamedTypeSymbol interfaceSymbol)
            return null;

        foreach (var attribute in interfaceSymbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass)
                continue;

            if (attributeClass.Name != "TreeVisitorAttribute")
                continue;

            if (attributeClass.TypeArguments.Length != 1)
                continue;

            var nodeType = attributeClass.TypeArguments[0];
            var returnType = GetVisitorReturnType(interfaceSymbol);

            return new VisitorTarget(interfaceSymbol, nodeType, returnType);
        }

        return null;
    }

    private static ITypeSymbol? GetVisitorReturnType(INamedTypeSymbol interfaceSymbol)
    {
        if (interfaceSymbol.TypeArguments.Length == 0)
            return null;

        return interfaceSymbol.TypeArguments[0];
    }

    private static void Execute(
        Compilation compilation,
        ImmutableArray<VisitorTarget?> rawTargets,
        SourceProductionContext context)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var rawTarget in rawTargets)
        {
            if (rawTarget is null)
                continue;

            var target = rawTarget.Value;
            if (!seen.Add(target.InterfaceSymbol))
                continue;

            if (!IsPartial(target.InterfaceSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    _interfaceMustBePartial,
                    target.InterfaceSymbol.Locations.FirstOrDefault(),
                    target.InterfaceSymbol.ToDisplayString()));
                continue;
            }

            var descendants = GetCandidateTypes(compilation)
                .Where(t => IsVisitableNode(t, target.NodeType))
                .OrderBy(t => t.ToDisplayString(_typeNameFormat), StringComparer.Ordinal)
                .ToImmutableArray();

            var source = GenerateSource(target, descendants);
            var hintName = GetHintName(target.InterfaceSymbol);

            context.AddSource(hintName, SourceText.From(source, Encoding.UTF8));
        }
    }

    private static string GenerateSource(VisitorTarget target, ImmutableArray<INamedTypeSymbol> descendants)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!target.InterfaceSymbol.ContainingNamespace.IsGlobalNamespace)
        {
            sb.Append("namespace ").Append(target.InterfaceSymbol.ContainingNamespace.ToDisplayString()).AppendLine(";");
            sb.AppendLine();
        }

        var containingTypes = GetContainingTypesOutsideIn(target.InterfaceSymbol);

        foreach (var containingType in containingTypes)
        {
            sb.Append(GetIndent(containingType.Depth))
              .Append(GetTypeHeader(containingType.Symbol))
              .AppendLine();
            sb.Append(GetIndent(containingType.Depth)).AppendLine("{");
        }

        var interfaceIndent = GetIndent(containingTypes.Count);
        sb.Append(interfaceIndent)
          .Append(GetInterfaceHeader(target.InterfaceSymbol))
          .AppendLine();
        sb.Append(interfaceIndent).AppendLine("{");

        var memberIndent = GetIndent(containingTypes.Count + 1);
        var returnTypeText = target.ReturnType?.ToDisplayString(_typeNameFormat) ?? "void";
        var nodeTypeText = target.NodeType.ToDisplayString(_typeNameFormat);

        var baseVisitAlreadyExists = HasVisitMethod(target.InterfaceSymbol, target.NodeType);

        var childTypes = descendants
            .Where(t => !SymbolEqualityComparer.Default.Equals(t, target.NodeType))
            .ToImmutableArray();

        foreach (var child in childTypes)
        {
            if (HasVisitMethod(target.InterfaceSymbol, child))
                continue;

            sb.Append(memberIndent)
              .Append(returnTypeText)
              .Append(" Visit(")
              .Append(child.ToDisplayString(_typeNameFormat))
              .AppendLine(" node);");
        }

        if (childTypes.Length > 0 && !baseVisitAlreadyExists)
        {
            sb.AppendLine();

            if (target.ReturnType is null)
            {
                AppendVoidDispatcher(sb, memberIndent, nodeTypeText, childTypes, !target.NodeType.IsValueType);
            }
            else
            {
                AppendReturnDispatcher(sb, memberIndent, nodeTypeText, returnTypeText, childTypes, !target.NodeType.IsValueType);
            }
        }
        else if (childTypes.Length == 0 && !baseVisitAlreadyExists)
        {
            sb.AppendLine();

            if (target.ReturnType is null)
            {
                sb.Append(memberIndent)
                  .Append("void Visit(")
                  .Append(nodeTypeText)
                  .AppendLine(" node)")
                  .Append(memberIndent).AppendLine("{")
                  .Append(memberIndent).Append("    throw new global::System.ArgumentOutOfRangeException(nameof(node), node, \"No visitable node types were found.\");").AppendLine()
                  .Append(memberIndent).AppendLine("}");
            }
            else
            {
                sb.Append(memberIndent)
                  .Append(returnTypeText)
                  .Append(" Visit(")
                  .Append(nodeTypeText)
                  .AppendLine(" node)")
                  .Append(memberIndent).Append("    => throw new global::System.ArgumentOutOfRangeException(nameof(node), node, \"No visitable node types were found.\");").AppendLine();
            }
        }

        sb.Append(interfaceIndent).AppendLine("}");

        for (var i = containingTypes.Count - 1; i >= 0; i--)
        {
            sb.Append(GetIndent(containingTypes[i].Depth)).AppendLine("}");
        }

        return sb.ToString();
    }

    private static void AppendVoidDispatcher(
        StringBuilder sb,
        string indent,
        string nodeTypeText,
        ImmutableArray<INamedTypeSymbol> childTypes,
        bool addNullGuard)
    {
        sb.Append(indent)
          .Append("void Visit(")
          .Append(nodeTypeText)
          .AppendLine(" node)")
          .Append(indent).AppendLine("{");

        if (addNullGuard)
        {
            sb.Append(indent).Append("    if (node is null) throw new global::System.ArgumentNullException(nameof(node));").AppendLine();
            sb.AppendLine();
        }

        sb.Append(indent).AppendLine("    switch (node)");
        sb.Append(indent).AppendLine("    {");

        foreach (var child in childTypes)
        {
            var childTypeText = child.ToDisplayString(_typeNameFormat);
            sb.Append(indent).Append("        case ").Append(childTypeText).AppendLine(" typedNode:")
              .Append(indent).AppendLine("            Visit(typedNode);")
              .Append(indent).AppendLine("            break;");
        }

        sb.Append(indent).AppendLine("        default:")
          .Append(indent).AppendLine("            throw new global::System.ArgumentOutOfRangeException(nameof(node), node, \"No Visit overload exists for the runtime node type.\");")
          .Append(indent).AppendLine("    }")
          .Append(indent).AppendLine("}");
    }

    private static void AppendReturnDispatcher(
        StringBuilder sb,
        string indent,
        string nodeTypeText,
        string returnTypeText,
        ImmutableArray<INamedTypeSymbol> childTypes,
        bool addNullGuard)
    {
        sb.Append(indent)
          .Append(returnTypeText)
          .Append(" Visit(")
          .Append(nodeTypeText)
          .AppendLine(" node)")
          .Append(indent).AppendLine("    => node switch")
          .Append(indent).AppendLine("    {");

        foreach (var child in childTypes)
        {
            var childTypeText = child.ToDisplayString(_typeNameFormat);
            sb.Append(indent)
              .Append("        ")
              .Append(childTypeText)
              .Append(" typedNode => Visit(typedNode),")
              .AppendLine();
        }

        if (addNullGuard)
        {
            sb.Append(indent).AppendLine("        null => throw new global::System.ArgumentNullException(nameof(node)),");
        }

        sb.Append(indent).AppendLine("        _ => throw new global::System.ArgumentOutOfRangeException(nameof(node), node, \"No Visit overload exists for the runtime node type.\"),")
          .Append(indent).AppendLine("    };");
    }

    private static bool IsPartial(INamedTypeSymbol type)
        => type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<InterfaceDeclarationSyntax>()
            .Any(s => s.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)));

    private static bool HasVisitMethod(INamedTypeSymbol interfaceSymbol, ITypeSymbol parameterType)
        => interfaceSymbol
            .GetMembers("Visit")
            .OfType<IMethodSymbol>()
            .Any(m =>
                m.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, parameterType));

    private static bool IsVisitableNode(INamedTypeSymbol candidate, ITypeSymbol root)
    {
        if (candidate.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return false;

        if (candidate.IsStatic)
            return false;

        if (SymbolEqualityComparer.Default.Equals(candidate, root))
            return false;

        return IsAssignableTo(candidate, root);
    }

    private static bool IsAssignableTo(ITypeSymbol candidate, ITypeSymbol root)
    {
        if (SymbolEqualityComparer.Default.Equals(candidate, root))
            return true;

        if (root.TypeKind == TypeKind.Interface)
        {
            foreach (var iface in candidate.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, root))
                    return true;
            }
        }

        var current = candidate.BaseType;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, root))
                return true;

            current = current.BaseType;
        }

        return false;
    }

    private static List<INamedTypeSymbol> GetCandidateTypes(Compilation compilation)
    {
        var result = new List<INamedTypeSymbol>();
        CollectTypes(compilation.GlobalNamespace, result);
        return result;
    }

    private static void CollectTypes(INamespaceSymbol ns, List<INamedTypeSymbol> result)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                CollectTypes(childNamespace, result);
            }
            else if (member is INamedTypeSymbol type)
            {
                CollectTypes(type, result);
            }
        }
    }

    private static void CollectTypes(INamedTypeSymbol type, List<INamedTypeSymbol> result)
    {
        result.Add(type);

        foreach (var nested in type.GetTypeMembers())
        {
            CollectTypes(nested, result);
        }
    }

    private static string GetHintName(INamedTypeSymbol interfaceSymbol)
        => interfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "")
            .Replace('<', '[')
            .Replace('>', ']')
            .Replace('.', '_')
            .Replace(':', '_')
          + ".TreeVisitor.g.cs";

    private static List<ContainingTypeInfo> GetContainingTypesOutsideIn(INamedTypeSymbol interfaceSymbol)
    {
        var stack = new Stack<INamedTypeSymbol>();
        var current = interfaceSymbol.ContainingType;

        while (current is not null)
        {
            stack.Push(current);
            current = current.ContainingType;
        }

        var result = new List<ContainingTypeInfo>();
        var depth = 0;

        while (stack.Count > 0)
        {
            result.Add(new ContainingTypeInfo(stack.Pop(), depth));
            depth++;
        }

        return result;
    }

    private static string GetInterfaceHeader(INamedTypeSymbol symbol)
    {
        var accessibility = GetAccessibility(symbol);
        var typeParams = GetTypeParameterList(symbol);
        var constraints = GetConstraintClauses(symbol);

        return $"{accessibility}partial interface {symbol.Name}{typeParams}{constraints}";
    }

    private static string GetTypeHeader(INamedTypeSymbol symbol)
    {
        var accessibility = GetAccessibility(symbol);

        string kind =
            symbol.IsRecord
                ? (symbol.TypeKind == TypeKind.Struct ? "record struct" : "record")
                : symbol.TypeKind switch
                {
                    TypeKind.Class => "class",
                    TypeKind.Struct => symbol.IsReadOnly ? "readonly struct" : "struct",
                    TypeKind.Interface => "interface",
                    _ => "class"
                };

        var modifiers = new List<string>();

        if (!string.IsNullOrWhiteSpace(accessibility))
            modifiers.Add(accessibility);

        if (symbol is { TypeKind: TypeKind.Class, IsRecord: false })
        {
            if (symbol.IsStatic)
            {
                modifiers.Add("static");
            }
            else
            {
                if (symbol.IsAbstract)
                    modifiers.Add("abstract");
                if (symbol.IsSealed)
                    modifiers.Add("sealed");
            }
        }

        if (symbol is { IsRecord: true, TypeKind: TypeKind.Class })
        {
            if (symbol.IsAbstract)
                modifiers.Add("abstract");
            if (symbol.IsSealed)
                modifiers.Add("sealed");
        }

        modifiers.Add("partial");
        modifiers.Add(kind);
        modifiers.Add(symbol.Name + GetTypeParameterList(symbol));

        return string.Join(" ", modifiers) + GetConstraintClauses(symbol);
    }

    private static string GetTypeParameterList(INamedTypeSymbol symbol)
    {
        if (symbol.TypeParameters.Length == 0)
            return string.Empty;

        return "<" + string.Join(", ", symbol.TypeParameters.Select(tp => tp.Variance switch
        {
            VarianceKind.In => $"in {tp.Name}",
            VarianceKind.Out => $"out {tp.Name}",
            _ => tp.Name
        })) + ">";
    }

    private static string GetConstraintClauses(INamedTypeSymbol symbol)
    {
        if (symbol.TypeParameters.Length == 0)
            return string.Empty;

        var clauses = new List<string>();

        foreach (var tp in symbol.TypeParameters)
        {
            var parts = new List<string>();

            if (tp.HasReferenceTypeConstraint)
                parts.Add("class");

            if (tp.HasUnmanagedTypeConstraint)
                parts.Add("unmanaged");

            if (tp.HasValueTypeConstraint)
                parts.Add("struct");

            foreach (var constraintType in tp.ConstraintTypes)
            {
                parts.Add(constraintType.ToDisplayString(_typeNameFormat));
            }

            if (tp.HasNotNullConstraint)
                parts.Add("notnull");

            if (tp.HasConstructorConstraint)
                parts.Add("new()");

            if (parts.Count > 0)
            {
                clauses.Add($" where {tp.Name} : {string.Join(", ", parts)}");
            }
        }

        return string.Concat(clauses);
    }

    private static string GetAccessibility(INamedTypeSymbol symbol)
        => symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public ",
            Accessibility.Internal => "internal ",
            Accessibility.Private => "private ",
            Accessibility.Protected => "protected ",
            Accessibility.ProtectedAndInternal => "protected internal ",
            Accessibility.ProtectedOrInternal => "private protected ",
            _ => string.Empty
        };

    private static string GetIndent(int level) => new(' ', level * 4);

    private record struct VisitorTarget(
        INamedTypeSymbol InterfaceSymbol,
        ITypeSymbol NodeType,
        ITypeSymbol? ReturnType);

    private record struct ContainingTypeInfo(INamedTypeSymbol Symbol, int Depth);
}