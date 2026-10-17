using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed record SymbolReference(SourceLocation Location, Symbol Symbol, string Description)
{
	public SourceLocation? Declaration { get; init; } = SemanticModel.FindDeclaration(Symbol);
}

public sealed class SemanticModel
{
	private const string NodeNamespace = "Cella.Core.Binding.Nodes";
	
	private static readonly StringComparer _pathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	
	private readonly Dictionary<string, List<SymbolReference>> _references = new(_pathComparer);
	private readonly Dictionary<Type, PropertyInfo[]> _children = [];
	private readonly Dictionary<TypeParameterSymbol, SourceLocation> _typeParameters = [];
	
	private readonly Dictionary<IExpressionNode, AccessExpressionNode> _accesses =
		new(ReferenceEqualityComparer.Instance);
	
	public SemanticModel(IEnumerable<ResolvedFileNode> files, IEnumerable<(Token Name, Symbol Symbol)> names)
	{
		foreach (var file in files)
			Walk(file);
		
		var latest = new Dictionary<SourceLocation, Symbol>();
		foreach (var (name, symbol) in names)
			latest[name.SourceLocation] = symbol;
		
		foreach (var (location, symbol) in latest)
			Add(location, symbol,
				symbol is TraitSymbol trait ? $"{trait.Name}: trait" : DescribeType((TypeSymbol)symbol));
		
		foreach (var references in _references.Values)
		{
			for (var i = 0; i < references.Count; i++)
			{
				if (references[i].Symbol is TypeParameterSymbol parameter &&
				    _typeParameters.TryGetValue(parameter, out var declaration))
					references[i] = references[i] with { Declaration = declaration };
			}
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
	
	public IReadOnlyList<SymbolReference> GetReferences(string path) =>
		_references.TryGetValue(path, out var references) ? references : [];
	
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
		if (node is IResolvedExpressionNode { Syntax: var syntax } && FindAccess(syntax) is { } accessed)
			_accesses[accessed.Target] = accessed;
		
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
			
			case ResolvedFunctionCallExpressionNode { IsConversion: false } n:
				Add(FindCallName(n.Syntax, n.Function.Symbol) ?? FindDereferenceDot(n), n.Function.Symbol,
					DescribeFunction(n.Function));
				
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
				AddTypeParameters(n.Symbol.Node.TypeParameters, n.Symbol.TypeParameters);
				foreach (var field in n.Symbol.Members.OfType<FieldSymbol>())
				{
					if (field.Node is { } fieldNode)
						Add(fieldNode.Identifier, field, $"{field.Name}: {fieldNode.Type.SourceLocation.GetText()}");
				}
				
				break;
			
			case ResolvedEnumNode n:
				AddType(n.Symbol, n.Symbol.Node.Identifier);
				AddTypeParameters(n.Symbol.Node.TypeParameters, n.Symbol.TypeParameters);
				foreach (var enumCase in n.Symbol.Cases)
					Add(enumCase.Node.Identifier, enumCase, DescribeCase(n.Symbol, enumCase));
				
				break;
			
			case ResolvedTraitNode n:
				Add(n.Symbol.Node.Identifier, n.Symbol, $"{n.Symbol.Name}: trait");
				AddTypeParameters(n.Symbol.Node.TypeParameters, n.Symbol.TypeParameters);
				foreach (var method in n.Symbol.Functions)
				{
					if (method.Function.Syntax is IFunctionNode function)
						Add(function.Identifier, method, $"{method.Name}: fun");
				}
				
				break;
		}
	}
	
	private void AddLocal(LocalVariableSymbol local) =>
		Add(local.Identifier, local, DescribeVariable(local, local.Type));
	
	private void AddType(NamedTypeSymbol type, Token identifier) => Add(identifier, type, DescribeType(type));
	
	private void AddTypeParameters(ImmutableArray<TypeParameterNode> nodes, ImmutableArray<TypeParameterSymbol> symbols)
	{
		foreach (var node in nodes)
		{
			if (symbols.FirstOrDefault(symbol => symbol.Name == node.Identifier.Text) is not { } symbol)
				continue;
			
			_typeParameters[symbol] = node.Identifier.SourceLocation;
			Add(node.Identifier, symbol, DescribeType(symbol));
		}
	}
	
	private void AddFunction(FunctionInfo info)
	{
		Add(FindDeclaration(info.Symbol) is { } declaration ? declaration : SourceLocation.None, info.Symbol,
			DescribeFunction(info));
		
		if (info.Symbol.Syntax is FunctionNode function)
			AddTypeParameters(function.TypeParameters, info.Symbol.DeclaredTypeParameters);
		
		var parameters = info.Symbol.Parameters;
		for (var i = 0; i < parameters.Length && i < info.Signature.ParameterTypes.Length; i++)
			Add(parameters[i].Definition, parameters[i], DescribeParameter(parameters[i].Name, info.Signature, i));
	}
	
	private static Token? FindCallName(IExpressionNode syntax, FunctionSymbol function) => syntax switch
	{
		BinaryOpExpressionNode binary => IsOperator(function) ? binary.Op : null,
		UnaryOpExpressionNode unary => IsOperator(function) ? unary.Op : null,
		IndexerExpressionNode => null,
		_ => IsOperator(function) ? null : FindName(syntax)
	};
	
	private static AccessExpressionNode? FindAccess(IExpressionNode syntax) => syntax switch
	{
		AccessExpressionNode access => access,
		CallExpressionNode { Target: AccessExpressionNode access } => access,
		_ => null
	};
	
	private Token? FindDereferenceDot(ResolvedFunctionCallExpressionNode call) =>
		IsOperator(call.Function.Symbol) && call.Function.Symbol.DeclaredName == "*" &&
		_accesses.TryGetValue(call.Syntax, out var access)
			? access.Dot
			: null;
	
	private static bool IsOperator(FunctionSymbol function) =>
		function.Syntax is FunctionNode node && TokenTypeInfo.GetOperator(node.Identifier.Text) is not null;
	
	private static Token? FindName(IExpressionNode syntax) => syntax switch
	{
		VarExpressionNode variable => variable.Identifier,
		AccessExpressionNode access => access.Member,
		CallExpressionNode call => FindName(call.Target),
		IndexerExpressionNode indexer => FindName(indexer.Target),
		_ => null
	};
	
	public static string Describe(CompletionCandidate candidate) => candidate switch
	{
		{ Function: { } function } => DescribeFunction(function),
		{ Symbol: ParameterSymbol parameter, Type: { } type } => $"{parameter.Name}: {TypeName(type)}",
		{ Symbol: LocalVariableSymbol local } => DescribeVariable(local, local.Type),
		{ Symbol: GlobalSymbol global } =>
			$"{global.Name}: {global.Syntax.Keyword.Text} {global.Syntax.Type.SourceLocation.GetText()}",
		{ Symbol: FieldSymbol { Node: { } node } field } => $"{field.Name}: {node.Type.SourceLocation.GetText()}",
		{ Symbol: PropertySymbol property } => $"{property.Name}: prop",
		{ Symbol: MethodSymbol method } => $"{method.Name}: fun",
		{ Symbol: FunctionSymbol function } => $"{function.DeclaredName}: fun",
		{ Symbol: TraitSymbol trait } => $"{trait.Name}: trait",
		{ Symbol: TypeSymbol type } => DescribeType(type),
		{ Symbol: ModulePathSymbol module } => $"mod {module.Path}",
		_ => candidate.Symbol.Name
	};
	
	private static string DescribeVariable(Symbol symbol, TypeSymbol type) => symbol switch
	{
		LocalVariableSymbol { IsMutable: true } => $"var {symbol.Name}: {TypeName(type)}",
		LocalVariableSymbol { IsConstant: true } => $"const {symbol.Name}: {TypeName(type)}",
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
	
	private static string DescribeFunction(FunctionInfo info) => DescribeSignature(info, false).Label;
	
	public static IEnumerable<int> GetCallParameters(FunctionInfo info) =>
		Enumerable.Range(0, info.Signature.ParameterTypes.Length).Where(i =>
			i >= info.Symbol.Parameters.Length || info.Symbol.Parameters[i].Name != "self");
	
	public static (string Label, ImmutableArray<(int Start, int End)> Parameters) DescribeSignature(FunctionInfo info,
		bool isCall)
	{
		var signature = info.Signature;
		var parameters = info.Symbol.Parameters;
		var keyword = info.Symbol.Syntax is FunctionNode function
			? function.Identifier.Type switch
			{
				TokenType.Identifier => "fun",
				TokenType.KeywordGet => "get",
				TokenType.KeywordSet => "set",
				_ => "op"
			}
			: "fun";
		
		var label = new StringBuilder($"{info.Symbol.DeclaredName}: {keyword}");
		var ranges = ImmutableArray.CreateBuilder<(int Start, int End)>();
		var indices = isCall
			? GetCallParameters(info).ToArray()
			: [..Enumerable.Range(0, signature.ParameterTypes.Length)];
		
		if (isCall || indices.Length > 0)
		{
			label.Append('(');
			foreach (var i in indices)
			{
				if (ranges.Count > 0)
					label.Append(", ");
				
				var start = label.Length;
				label.Append(DescribeParameter(i < parameters.Length ? parameters[i].Name : "_", signature, i));
				ranges.Add((start, label.Length));
			}
			
			label.Append(')');
		}
		
		if (signature.ReturnType != NativeSymbols.Void)
			label.Append(" -> ").Append(TypeName(signature.ReturnType));
		
		return (label.ToString(), ranges.ToImmutable());
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
	
	private static string RefPrefix(NamedTypeSymbol type) => type.IsRef ? "ref " : "";
	
	private static string DescribeType(TypeSymbol type) => type switch
	{
		RecordSymbol { IsUnion: true } record => $"{TypeName(type)}: {RefPrefix(record)}union",
		RecordSymbol record => $"{TypeName(type)}: {RefPrefix(record)}rec",
		EnumSymbol enumType => $"{TypeName(type)}: {RefPrefix(enumType)}enum",
		TraitType trait => $"{trait.Name}: trait",
		TypeParameterSymbol { ValueType: { } valueType } => $"{type.Name}: const {TypeName(valueType)}",
		_ => TypeName(type)
	};
}