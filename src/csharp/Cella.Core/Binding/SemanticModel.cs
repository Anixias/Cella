using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed record SymbolReference(SourceLocation Location, Symbol Symbol, string Description)
{
	public SourceLocation? Declaration { get; } = SemanticModel.FindDeclaration(Symbol);
}

public sealed class SemanticModel
{
	private const string NodeNamespace = "Cella.Core.Binding.Nodes";
	
	private static readonly StringComparer _pathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	
	private readonly Dictionary<string, List<SymbolReference>> _references = new(_pathComparer);
	private readonly Dictionary<Type, PropertyInfo[]> _children = [];
	
	public SemanticModel(IEnumerable<ResolvedFileNode> files, IEnumerable<(ITypeNode Node, TypeSymbol Type)> types)
	{
		foreach (var file in files)
			Walk(file);
		
		foreach (var (node, type) in types)
		{
			var token = node switch
			{
				IdentifierTypeNode identifier => identifier.Token,
				GenericTypeNode generic => generic.Identifier,
				QualifiedTypeNode qualified => qualified.Parts[^1],
				_ => (Token?)null
			};
			
			if (token is { } name)
				Add(name.SourceLocation, type, DescribeType(type));
		}
	}
	
	public SymbolReference? Find(string path, int offset)
	{
		if (!_references.TryGetValue(path, out var references))
			return null;
		
		SymbolReference? best = null;
		foreach (var reference in references)
		{
			var range = reference.Location.Range;
			if (offset >= range.Start && offset <= range.End &&
			    (best is null || range.Length < best.Location.Range.Length))
				best = reference;
		}
		
		return best;
	}
	
	public static SourceLocation? FindDeclaration(Symbol symbol)
	{
		SourceLocation? location = symbol switch
		{
			FunctionSymbol { Syntax: IFunctionNode function } => function.Identifier.SourceLocation,
			FunctionSymbol function => function.Syntax.SourceLocation,
			MethodSymbol method => FindDeclaration(method.Function),
			GlobalSymbol global => global.Syntax.Identifier.SourceLocation,
			LocalVariableSymbol local => local.Identifier.SourceLocation,
			ParameterSymbol parameter => parameter.Definition,
			FieldSymbol { Node: { } field } => field.Identifier.SourceLocation,
			PropertySymbol { Node: { } property } => property.Identifier.SourceLocation,
			EnumCaseSymbol enumCase => enumCase.Node.Identifier.SourceLocation,
			TraitSymbol trait => trait.Node.Identifier.SourceLocation,
			TraitType trait => trait.Trait.Node.Identifier.SourceLocation,
			TypeSymbol type when type.OriginalDefinition is RecordSymbol record =>
				record.Node.Identifier.SourceLocation,
			TypeSymbol type when type.OriginalDefinition is EnumSymbol enumType => enumType.Node.Identifier
				.SourceLocation,
			_ => null
		};
		
		return location is { Source.FilePath.Length: > 0 } ? location : null;
	}
	
	private void Add(SourceLocation location, Symbol symbol, string description)
	{
		var path = location.Source.FilePath;
		if (path.Length == 0 || !location.Range.IsValid)
			return;
		
		if (!_references.TryGetValue(path, out var references))
			_references[path] = references = [];
		
		references.Add(new(location, symbol, description));
	}
	
	private void Add(Token? token, Symbol symbol, string description)
	{
		if (token is { } name)
			Add(name.SourceLocation, symbol, description);
	}
	
	private void Walk(object root)
	{
		var pending = new Stack<object>([root]);
		var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
		while (pending.TryPop(out var node))
		{
			if (!seen.Add(node))
				continue;
			
			Visit(node);
			foreach (var property in GetChildProperties(node.GetType()))
			{
				switch (property.GetValue(node))
				{
					case null or string:
						break;
					
					case IEnumerable items when !IsDefaultArray(items):
						foreach (var item in items)
							PushNodes(pending, item);
						
						break;
					
					case var value:
						PushNodes(pending, value);
						break;
				}
			}
		}
	}
	
	private static void PushNodes(Stack<object> pending, object? value)
	{
		if (value is ITuple tuple)
		{
			for (var i = 0; i < tuple.Length; i++)
				PushNodes(pending, tuple[i]);
		}
		else if (value is not null && IsNode(value))
			pending.Push(value);
	}
	
	private PropertyInfo[] GetChildProperties(Type type)
	{
		if (!_children.TryGetValue(type, out var properties))
			_children[type] = properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
				.Where(static property => property.GetIndexParameters().Length == 0 && property.Name != "Syntax")
				.ToArray();
		
		return properties;
	}
	
	private static bool IsNode(object value) => value.GetType().Namespace?.StartsWith(NodeNamespace) == true;
	
	private static bool IsDefaultArray(IEnumerable items) =>
		items.GetType() is { IsGenericType: true } type &&
		type.GetGenericTypeDefinition() == typeof(ImmutableArray<>) &&
		type.GetProperty(nameof(ImmutableArray<object>.IsDefault))!.GetValue(items) is true;
	
	private void Visit(object node)
	{
		switch (node)
		{
			case ResolvedVarExpressionNode n:
				Add(FindName(n.Syntax), n.Symbol, DescribeVariable(n.Symbol, n.Type));
				break;
			
			case ResolvedGlobalExpressionNode n:
				Add(FindName(n.Syntax), n.Symbol, DescribeVariable(n.Symbol, n.Type));
				break;
			
			case ResolvedTraitConstantExpressionNode n:
				Add(FindName(n.Syntax), n.Constant, DescribeVariable(n.Constant, n.Type));
				break;
			
			case ResolvedFunctionCallExpressionNode n:
				Add(FindName(n.Syntax), n.Function.Symbol, DescribeFunction(n.Function));
				break;
			
			case ResolvedFunctionReferenceExpressionNode n:
				Add(FindName(n.Syntax), n.Function.Symbol, DescribeFunction(n.Function));
				break;
			
			case ResolvedRecordExpressionNode n:
				Add(FindName(n.Syntax), n.Type, DescribeType(n.Type));
				break;
			
			case ResolvedConstructorCallExpressionNode n:
				Add(FindName(n.Syntax), n.Type, DescribeType(n.Type));
				break;
			
			case ResolvedAccessExpressionNode { Syntax: AccessExpressionNode access } n:
				Add(access.Member, n.Member, DescribeMember(n.Member, n.Type));
				break;
			
			case ResolvedPropertyExpressionNode n:
				Add(FindName(n.Syntax), n.Property, $"{n.Property.Name}: prop {TypeName(n.Type)}");
				break;
			
			case ResolvedEnumCaseExpressionNode n:
				Add(FindName(n.Syntax), n.Case, DescribeCase(n.Type, n.Case));
				break;
			
			case ResolvedCaseNameExpressionNode { Symbol: { } symbol } n:
				Add(n.Name, symbol, symbol.Name);
				break;
			
			case ResolvedValueParameterExpressionNode n:
				Add(FindName(n.Syntax), n.Parameter, n.Parameter.Name);
				break;
			
			case ResolvedVarStatementNode n:
				AddLocal(n.Symbol);
				break;
			
			case ResolvedPattern pattern:
				foreach (var binding in pattern.Bindings.Append(pattern.TypeBinding).OfType<LocalVariableSymbol>())
					AddLocal(binding);
				
				break;
			
			case ResolvedFunctionNode { Syntax: FunctionNode } n:
				AddFunction(n.FunctionInfo);
				break;
			
			case ResolvedExternalFunctionNode n:
				AddFunction(n.FunctionInfo);
				break;
			
			case ResolvedGlobalNode n:
				Add(n.Info.Symbol.Syntax.Identifier, n.Info.Symbol, DescribeVariable(n.Info.Symbol, n.Info.Type));
				break;
			
			case ResolvedRecordNode n:
				AddType(n.Symbol, n.Symbol.Node.Identifier);
				foreach (var field in n.Symbol.Members.OfType<FieldSymbol>())
				{
					if (field.Node is { } fieldNode)
						Add(fieldNode.Identifier, field, $"{field.Name}: {fieldNode.Type.SourceLocation.GetText()}");
				}
				
				break;
			
			case ResolvedEnumNode n:
				AddType(n.Symbol, n.Symbol.Node.Identifier);
				foreach (var enumCase in n.Symbol.Cases)
					Add(enumCase.Node.Identifier, enumCase, DescribeCase(n.Symbol, enumCase));
				
				break;
			
			case ResolvedTraitNode n:
				Add(n.Symbol.Node.Identifier, n.Symbol, $"{n.Symbol.Name}: trait");
				break;
		}
	}
	
	private void AddLocal(LocalVariableSymbol local) =>
		Add(local.Identifier, local, DescribeVariable(local, local.Type));
	
	private void AddType(NamedTypeSymbol type, Token identifier) => Add(identifier, type, DescribeType(type));
	
	private void AddFunction(FunctionInfo info)
	{
		Add(FindDeclaration(info.Symbol) is { } declaration ? declaration : SourceLocation.None, info.Symbol,
			DescribeFunction(info));
		
		var parameters = info.Symbol.Parameters;
		for (var i = 0; i < parameters.Length && i < info.Signature.ParameterTypes.Length; i++)
			Add(parameters[i].Definition, parameters[i], DescribeParameter(parameters[i].Name, info.Signature, i));
	}
	
	private static Token? FindName(IExpressionNode syntax) => syntax switch
	{
		VarExpressionNode variable => variable.Identifier,
		AccessExpressionNode access => access.Member,
		CallExpressionNode call => FindName(call.Target),
		IndexerExpressionNode indexer => FindName(indexer.Target),
		_ => null
	};
	
	private static string DescribeVariable(Symbol symbol, TypeSymbol type) => symbol switch
	{
		LocalVariableSymbol { IsMutable: true } => $"var {symbol.Name}: {TypeName(type)}",
		LocalVariableSymbol => $"val {symbol.Name}: {TypeName(type)}",
		GlobalSymbol global => $"{symbol.Name}: {global.Syntax.Keyword.Text} {TypeName(type)}",
		_ => $"{symbol.Name}: {TypeName(type)}"
	};
	
	private static string DescribeMember(MemberSymbol member, TypeSymbol type) => member switch
	{
		MethodSymbol method => $"{method.Name}: fun",
		_ => $"{member.Name}: {TypeName(type)}"
	};
	
	private static string DescribeCase(TypeSymbol type, EnumCaseSymbol enumCase) =>
		$"{TypeName(type.OriginalDefinition)}.{enumCase.Name}";
	
	private static string DescribeFunction(FunctionInfo info)
	{
		var signature = info.Signature;
		var parameters = info.Symbol.Parameters;
		var described = Enumerable.Range(0, signature.ParameterTypes.Length)
			.Select(i => DescribeParameter(i < parameters.Length ? parameters[i].Name : "_", signature, i));
		
		var result = signature.ReturnType == NativeSymbols.Void ? "" : $" -> {TypeName(signature.ReturnType)}";
		var keyword = info.Symbol.Syntax is FunctionNode function
			? function.Identifier.Type switch
			{
				TokenType.Identifier => "fun",
				TokenType.KeywordGet => "get",
				TokenType.KeywordSet => "set",
				_ => "op"
			}
			: "fun";
		
		var parameterList = signature.ParameterTypes.IsEmpty ? "" : $"({string.Join(", ", described)})";
		return $"{info.Symbol.Name}: {keyword}{parameterList}{result}";
	}
	
	private static string DescribeParameter(string name, FunctionSignature signature, int index)
	{
		var mode = signature.GetMode(index) switch
		{
			ParameterMode.Mut => "mut ",
			ParameterMode.Own => "own ",
			ParameterMode.Imm => "imm ",
			_ => ""
		};
		
		return name == "self" ? $"{mode}self" : $"{mode}{name}: {TypeName(signature.GetDeclaredType(index))}";
	}
	
	private static string TypeName(TypeSymbol type) => DiagnosticReporter.DescribeType(type);
	
	private static string DescribeType(TypeSymbol type) => type switch
	{
		RecordSymbol { IsUnion: true } => $"{TypeName(type)}: union",
		RecordSymbol => $"{TypeName(type)}: rec",
		EnumSymbol => $"{TypeName(type)}: enum",
		TraitType trait => $"{trait.Name}: trait",
		_ => TypeName(type)
	};
}