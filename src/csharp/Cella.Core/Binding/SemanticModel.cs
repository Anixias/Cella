using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed record SymbolReference(SourceLocation Location, Symbol Symbol, string Description)
{
	public SourceLocation? Declaration { get; init; } = SemanticModel.FindDeclaration(Symbol);
	public ImmutableArray<SourceLocation> OverloadDeclarations { get; init; } = [];
}

public sealed class SemanticModel
{
	private const string NodeNamespace = "Cella.Core.Binding.Nodes";
	
	private static readonly StringComparer _pathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	
	private readonly Dictionary<string, List<SymbolReference>> _references = new(_pathComparer);
	private readonly Dictionary<Type, PropertyInfo[]> _children = [];
	private readonly Func<FunctionSymbol, FunctionInfo?>? _functions;
	private readonly Func<GlobalSymbol, GlobalInfo?>? _globals;
	private readonly Func<TypeConstantUse, (GlobalSymbol Declared, Constant? Value)?>? _typeConstants;
	private readonly Func<EnumSymbol, EnumCaseSymbol, BigInteger>? _caseValues;
	private readonly Func<FileSymbol, ImportEnvironment?>? _imports;
	private readonly List<SourceLocation> _inactiveCalls = [];
	private readonly Lazy<Dictionary<string, Dictionary<TextRange, List<SymbolReference>>>> _usages;
	private readonly Lazy<Dictionary<string, SymbolReference>> _types;
	
	private readonly Dictionary<IExpressionNode, AccessExpressionNode> _accesses =
		new(ReferenceEqualityComparer.Instance);
	
	public SemanticModel(IEnumerable<ResolvedFileNode> files, IEnumerable<(Token Name, Symbol Symbol)> names,
		Func<FunctionSymbol, FunctionInfo?>? functions = null,
		Func<GlobalSymbol, GlobalInfo?>? globals = null,
		Func<TypeConstantUse, (GlobalSymbol Declared, Constant? Value)?>? typeConstants = null,
		Func<EnumSymbol, EnumCaseSymbol, BigInteger>? caseValues = null,
		Func<FileSymbol, ImportEnvironment?>? imports = null)
	{
		_imports = imports;
		_functions = functions;
		_globals = globals;
		_typeConstants = typeConstants;
		_caseValues = caseValues;
		_usages = new(IndexUsages);
		_types = new(IndexTypes);
		foreach (var file in files)
			Walk(file);
		
		var named = new Dictionary<SourceLocation, List<Symbol>>();
		foreach (var (name, symbol) in names)
		{
			if (symbol is FunctionSymbol && named.TryGetValue(name.SourceLocation, out var overloads) &&
			    overloads.All(static overload => overload is FunctionSymbol))
			{
				if (!overloads.Contains(symbol))
					overloads.Add(symbol);
			}
			else
				named[name.SourceLocation] = [symbol];
		}
		
		foreach (var (location, symbols) in named)
			Add(location, symbols[0], string.Join('\n', symbols.Select(symbol => DescribeNamed(symbol, functions))),
				symbols.Skip(1));
		
		var declared = new Dictionary<Symbol, string>(ReferenceEqualityComparer.Instance);
		foreach (var reference in _references.Values.SelectMany(static references => references))
		{
			if (reference.Symbol is LocalVariableSymbol or ParameterSymbol &&
			    reference.Declaration == reference.Location)
				declared.TryAdd(reference.Symbol, reference.Description);
		}
		
		foreach (var references in _references.Values)
		{
			for (var i = 0; i < references.Count; i++)
			{
				if (references[i].Symbol is TypeParameterSymbol { Definition: { } definition })
					references[i] = references[i] with { Declaration = definition.SourceLocation };
				else if (declared.TryGetValue(references[i].Symbol, out var description))
					references[i] = references[i] with { Description = description };
			}
		}
	}
	
	public SymbolReference? Find(string path, int offset)
	{
		if (!_references.TryGetValue(path, out var references))
			return null;
		
		SymbolReference? best = null;
		var bestContains = false;
		foreach (var reference in references)
		{
			var range = reference.Location.Range;
			if (offset < range.Start || offset > range.End)
				continue;
			
			var contains = offset < range.End;
			if (best is null || contains && !bestContains ||
			    contains == bestContains && range.Length < best.Location.Range.Length)
				(best, bestContains) = (reference, contains);
		}
		
		return best;
	}
	
	public IEnumerable<ImportUse> FindImportSources(FileSymbol file, Symbol symbol)
	{
		if (_imports?.Invoke(file) is not { } imports)
			return [];
		
		return symbol is TypeSymbol { OriginalDefinition: var original } && original != symbol
			? imports.SourcesOf(symbol).Concat(imports.SourcesOf(original))
			: imports.SourcesOf(symbol);
	}
	
	public IReadOnlyList<SymbolReference> GetReferences(string path) =>
		_references.TryGetValue(path, out var references) ? references : [];
	
	public IEnumerable<SymbolReference> GetReferences() =>
		_references.Values.SelectMany(static references => references);
	
	public IReadOnlyList<SourceLocation> InactiveCalls => _inactiveCalls;
	
	public IReadOnlyList<SymbolReference> GetUsages(SourceLocation declaration) =>
		_usages.Value.TryGetValue(declaration.Source.FilePath, out var byRange) &&
		byRange.TryGetValue(declaration.Range, out var usages)
			? usages
			: [];
	
	public SymbolReference? FindType(string name) => _types.Value.GetValueOrDefault(name);
	
	private Dictionary<string, SymbolReference> IndexTypes()
	{
		var types = new Dictionary<string, SymbolReference>();
		foreach (var reference in GetReferences())
		{
			if (reference.Symbol is NamedTypeSymbol or TraitSymbol or TraitType or TypeParameterSymbol)
				types.TryAdd(reference.Location.GetText().ToString(), reference);
		}
		
		return types;
	}
	
	private Dictionary<string, Dictionary<TextRange, List<SymbolReference>>> IndexUsages()
	{
		var usages = new Dictionary<string, Dictionary<TextRange, List<SymbolReference>>>(_pathComparer);
		foreach (var reference in GetReferences())
		{
			if (reference.Declaration is not { } declaration)
				continue;
			
			if (!usages.TryGetValue(declaration.Source.FilePath, out var byRange))
				usages[declaration.Source.FilePath] = byRange = [];
			
			byRange.GetOrAdd(declaration.Range).Add(reference);
		}
		
		return usages;
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
			ParameterSymbol { IsWritten: true } parameter => parameter.Definition,
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
	
	private void Add(SourceLocation location, Symbol symbol, string description, IEnumerable<Symbol>? overloads = null)
	{
		var path = location.Source.FilePath;
		if (path.Length == 0 || !location.Range.IsValid ||
		    symbol is InvalidType or LocalVariableSymbol { IsSynthesized: true })
			return;
		
		if (!_references.TryGetValue(path, out var references))
			_references[path] = references = [];
		
		references.Add(new(location, symbol, description)
		{
			OverloadDeclarations =
			[
				..(overloads ?? []).Select(FindDeclaration).Where(static declaration => declaration is not null)
				.Select(static declaration => declaration!.Value)
			]
		});
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
	
	private static bool IsDefaultArray(object items) =>
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
				Add(FindName(n.Syntax), n.Symbol, DescribeGlobal(n.Symbol, n.Type));
				break;
			
			case ResolvedTypeConstantExpressionNode n:
				Add(FindName(n.Syntax), n.Constant, DescribeTypeConstant(n));
				break;
			
			case ResolvedErasedCallExpressionNode n:
				if (n.Syntax.SourceLocation is { Source.FilePath.Length: > 0, Range.IsValid: true } call)
					_inactiveCalls.Add(call);
				
				break;
			
			case ResolvedFunctionCallExpressionNode { Function.Symbol.Property: { } property } n:
				Add(FindAccessorName(n.Syntax), property, DescribeProperty(property));
				break;
			
			case ResolvedFunctionCallExpressionNode { IsConversion: false } n:
				Add(FindCallName(n.Syntax, n.Function.Symbol) ?? FindDereferenceDot(n), n.Function.Symbol,
					DescribeFunction(n.Function));
				
				break;
			
			case ResolvedPropertyAssignmentExpressionNode { Setter.Symbol.Property: { } property } n:
				Add(FindAccessorName(n.Syntax), property, DescribeProperty(property));
				break;
			
			case ResolvedFunctionReferenceExpressionNode n:
				Add(FindName(n.Syntax), n.Function.Symbol, DescribeFunction(n.Function));
				break;
			
			case ResolvedFunctionGroupExpressionNode { Group.Functions: [var first, ..] functions } n when
				FindName(n.Syntax) is { } groupName:
				Add(groupName.SourceLocation, first.Symbol, string.Join('\n', functions.Select(DescribeFunction)),
					functions.Skip(1).Select(static function => function.Symbol));
				
				break;
			
			case ResolvedQuestionExpressionNode { Syntax: QuestionExpressionNode question } n:
				Add(question.Op, n.Operator, DescribeFunction(n.Info));
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
				Add(FindName(n.Syntax), n.Property, DescribeProperty(n.Property));
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
			
			case ResolvedVarStatementNode { Symbol.IsConstant: true, Syntax: VarStatementNode statement } n:
				Add(n.Symbol.Identifier, n.Symbol,
					DescribeConstant($"const {n.Symbol.Name}: {TypeName(n.Symbol.Type)}", statement.ExpressionNode,
						n.Symbol.ConstantValue));
				
				break;
			
			case ResolvedVarStatementNode n:
				AddLocal(n.Symbol);
				break;
			
			case ResolvedLoopVariable { Symbol: { IsLoopBinding: true } binding }:
				AddLocal(binding);
				break;
			
			case ResolvedRangeForStatementNode n:
				if (n.Counter.Identifier.Text is [not '.', ..])
					AddLocal(n.Counter);
				
				if (n.Index is { } rangeIndex)
					AddLocal(rangeIndex);
				
				break;
			
			case ResolvedCursorForStatementNode { Syntax: ForStatementNode loop } n:
				if (n.Index is { } cursorIndex)
					AddLocal(cursorIndex);
				
				if (loop.In is { } inKeyword)
					AddLoopOperator(inKeyword, n.Cursor.Value, TokenType.KeywordIn);
				
				AddLoopOperator(loop.Keyword, n.Step, TokenType.KeywordFor);
				break;
			
			case ResolvedPattern pattern:
				foreach (var binding in pattern.Bindings.Append(pattern.TypeBinding).OfType<LocalVariableSymbol>())
					AddLocal(binding);
				
				AddPatternNames(pattern);
				break;
			
			case ResolvedFunctionNode { Syntax: FunctionNode } n:
				AddFunction(n.FunctionInfo);
				break;
			
			case ResolvedFunctionNode { Syntax: LambdaDeclarationNode or ConstructorNode } n:
				AddParameters(n.FunctionInfo);
				break;
			
			case ResolvedExternalFunctionNode n:
				AddFunction(n.FunctionInfo);
				break;
			
			case ResolvedGlobalNode n:
				AddGlobal(n.Info.Symbol, n.Info);
				break;
			
			case ResolvedRecordNode n:
				AddType(n.Symbol, n.Symbol.Node.Identifier);
				AddTypeParameters(n.Symbol.Node.TypeParameters, n.Symbol.TypeParameters);
				foreach (var field in n.Symbol.Members.OfType<FieldSymbol>())
				{
					if (field.Node is { } fieldNode)
						Add(fieldNode.Identifier, field, DescribeField(field));
				}
				
				AddProperties(n.Symbol.Properties);
				break;
			
			case ResolvedEnumNode n:
				AddType(n.Symbol, n.Symbol.Node.Identifier);
				AddTypeParameters(n.Symbol.Node.TypeParameters, n.Symbol.TypeParameters);
				foreach (var enumCase in n.Symbol.Cases)
				{
					Add(enumCase.Node.Identifier, enumCase, DescribeCase(n.Symbol, enumCase));
					foreach (var field in enumCase.Fields)
					{
						if (field.Node is { } fieldNode)
							Add(fieldNode.Identifier, field, DescribeField(field));
					}
				}
				
				AddProperties(n.Symbol.Properties);
				break;
			
			case ResolvedTraitNode n:
				Add(n.Symbol.Node.Identifier, n.Symbol, $"{n.Symbol.Name}: trait");
				AddTypeParameters(n.Symbol.Node.TypeParameters, n.Symbol.TypeParameters);
				foreach (var method in n.Symbol.Functions)
				{
					if (method.Function.Syntax is IFunctionNode function)
						Add(function.Identifier, method, $"{method.Name}: fun");
					
					if (method.Function.Syntax is not FunctionNode { Body: null } bodyless)
						continue;
					
					AddTypeParameters(bodyless.TypeParameters, method.Function.DeclaredTypeParameters);
					if (_functions?.Invoke(method.Function) is { } info)
						AddParameters(info);
				}
				
				foreach (var constructor in n.Symbol.Constructors)
				{
					if (_functions?.Invoke(constructor) is { } info)
						AddParameters(info);
				}
				
				foreach (var constant in n.Symbol.Constants)
					AddGlobal(constant, _globals?.Invoke(constant));
				
				AddProperties(n.Symbol.Properties);
				break;
			
			case ResolvedImplNode n:
				AddTypeParameters(
				[
					..n.Symbol.Node.TypeParameters,
					..TypeParameterNode.CollectNestedLeaves(n.Symbol.Node.TypeParameters)
				], [..n.Symbol.TypeParameters.Where(n.Symbol.Declares)]);
				
				foreach (var constant in n.Symbol.Constants)
					AddGlobal(constant, _globals?.Invoke(constant));
				
				AddProperties(n.Symbol.Properties);
				
				break;
		}
	}
	
	private void AddLoopOperator(Token keyword, IResolvedExpressionNode call, TokenType kind)
	{
		if (call is ResolvedFunctionCallExpressionNode { Function: var info } &&
		    info.Symbol.Syntax is FunctionNode { Identifier.Type: var type } && type == kind)
			Add(keyword, info.Symbol, DescribeFunction(info));
	}
	
	private void AddPatternNames(ResolvedPattern pattern)
	{
		if (pattern.Syntax is not { } syntax)
			return;
		
		if (pattern is { Case: { } enumCase, EnumType: { } enumType })
			Add(syntax.CaseName, enumCase, DescribeCase(enumType, enumCase));
		
		foreach (var element in syntax.Elements)
		{
			if (element.Field is { } name && pattern.Fields.FirstOrDefault(field => field.Name == name.Text) is
				    { } field)
				Add(name, field, DescribeField(field));
		}
	}
	
	private void AddGlobal(GlobalSymbol global, GlobalInfo? info)
	{
		Add(global.Syntax.Identifier, global,
			info is { } known ? DescribeGlobal(global, known.Type) : $"{global.Name}: {global.Syntax.Keyword.Text}");
		
		if (info?.Initializer is { } initializer)
			Walk(initializer);
	}
	
	private void AddProperties(IEnumerable<PropertySymbol> properties)
	{
		foreach (var property in properties)
		{
			if (property.Node is { } node)
				Add(node.Identifier, property, DescribeProperty(property));
		}
	}
	
	private string DescribeProperty(PropertySymbol property)
	{
		var type = (property.Getter, property.Setter) switch
		{
			(FunctionAccessor { Function: var getter }, _) when _functions?.Invoke(getter) is { } info =>
				info.Signature.ReturnType,
			(_, FunctionAccessor { Function: var setter }) when _functions?.Invoke(setter) is { } info =>
				info.Signature.GetDeclaredType(info.Signature.ParameterTypes.Length - 1),
			_ => null
		};
		
		return type is null ? $"{property.Name}: prop" : $"{property.Name}: prop {TypeName(type)}";
	}
	
	private static Token? FindAccessorName(IExpressionNode syntax) =>
		FindName(syntax is BinaryOpExpressionNode assignment ? assignment.Left : syntax);
	
	private void AddLocal(LocalVariableSymbol local) =>
		Add(local.Identifier, local, DescribeVariable(local, local.Type));
	
	private void AddType(NamedTypeSymbol type, Token identifier) => Add(identifier, type, DescribeType(type));
	
	private void AddTypeParameters(ImmutableArray<TypeParameterNode> nodes, ImmutableArray<TypeParameterSymbol> symbols)
	{
		foreach (var node in nodes)
		{
			if (symbols.FirstOrDefault(symbol => symbol.Definition == node.Identifier) is { } symbol)
				Add(node.Identifier, symbol, DescribeType(symbol));
		}
	}
	
	private void AddFunction(FunctionInfo info)
	{
		Add(FindDeclaration(info.Symbol) is { } declaration ? declaration : SourceLocation.None, info.Symbol,
			DescribeFunction(info));
		
		if (info.Symbol.Syntax is FunctionNode function)
			AddTypeParameters(function.TypeParameters, info.Symbol.DeclaredTypeParameters);
		
		AddParameters(info);
	}
	
	private void AddParameters(FunctionInfo info)
	{
		var parameters = info.Symbol.Parameters;
		for (var i = 0; i < parameters.Length && i < info.Signature.ParameterTypes.Length; i++)
		{
			if (parameters[i].IsWritten)
				Add(parameters[i].Definition, parameters[i], DescribeParameter(parameters[i].Name, info.Signature, i));
		}
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
		function.Syntax is FunctionNode node && (TokenTypeInfo.GetOperator(node.Identifier.Text) is not null ||
		                                         node.Identifier.Type is TokenType.KeywordIn or TokenType.KeywordFor);
	
	private static Token? FindName(IExpressionNode syntax) => syntax switch
	{
		VarExpressionNode variable => variable.Identifier,
		AccessExpressionNode access => access.Member,
		CallExpressionNode call => FindName(call.Target),
		IndexerExpressionNode indexer => FindName(indexer.Target),
		PanicExpressionNode panic => panic.Keyword,
		_ => null
	};
	
	public static string Describe(CompletionCandidate candidate) => candidate switch
	{
		{ Function: { } function } => DescribeFunction(function),
		{ Symbol: LocalVariableSymbol local } => DescribeVariable(local, local.Type),
		{ Symbol: EnumCaseSymbol enumCase, Type: { } owner } => DescribeCaseName(owner, enumCase),
		{ Symbol: TypeSymbol type } => DescribeType(type),
		{ Symbol: ModulePathSymbol module } => $"mod {module.Path}",
		{ Symbol: FunctionSymbol function } => function.DeclaredName + DescribeTail(candidate),
		_ => candidate.Symbol.Name + DescribeTail(candidate)
	};
	
	public static string DescribeTail(CompletionCandidate candidate) => candidate switch
	{
		{ Function: { } function } => DescribeSignatureTail(function, false).Label,
		{ Symbol: ParameterSymbol, Type: { } type } => $": {TypeName(type)}",
		{ Symbol: LocalVariableSymbol local } => $": {VariableKeyword(local)} {TypeName(local.Type)}",
		{ Symbol: GlobalSymbol global } =>
			$": {global.Syntax.Keyword.Text} {global.Syntax.Type.SourceLocation.GetText()}",
		{ Symbol: FieldSymbol { Node: { } node } } => $": {node.Type.SourceLocation.GetText()}",
		{ Symbol: PropertySymbol, Type: { } type } => $": prop {TypeName(type)}",
		{ Symbol: PropertySymbol } => ": prop",
		{ Symbol: MethodSymbol or FunctionSymbol } => ": fun",
		{ Symbol: TraitSymbol } => ": trait",
		{ Symbol: EnumCaseSymbol, Type: { } owner } => $": {TypeName(owner.OriginalDefinition)}",
		{ Symbol: TypeSymbol type } => DescribeTypeTail(type),
		_ => ""
	};
	
	private static string VariableKeyword(LocalVariableSymbol local) => local switch
	{
		{ IsMutable: true } => "var",
		{ IsConstant: true } => "const",
		_ => "val"
	};
	
	private static string DescribeVariable(Symbol symbol, TypeSymbol type) => symbol switch
	{
		LocalVariableSymbol { IsBorrowBinding: true } local =>
			$"{VariableKeyword(local)} {symbol.Name}: {(local.IsMutBinding ? "mut" : "imm")}[{TypeName(
				type is PointerType { BaseType: var target } ? target : type)}]",
		LocalVariableSymbol local => $"{VariableKeyword(local)} {symbol.Name}: {TypeName(type)}",
		_ => $"{symbol.Name}: {TypeName(type)}"
	};
	
	private string DescribeGlobal(GlobalSymbol global, TypeSymbol type)
	{
		var head = $"{global.Name}: {global.Syntax.Keyword.Text} {TypeName(type)}";
		return global.IsConstant
			? DescribeConstant(head, global.Syntax.Initializer, _globals?.Invoke(global)?.Value)
			: head;
	}
	
	private string DescribeTypeConstant(ResolvedTypeConstantExpressionNode node)
	{
		var head = $"{node.Constant.Name}: const {TypeName(node.Type)}";
		return _typeConstants?.Invoke(node.Use) is var (declared, value)
			? DescribeConstant(head, declared.Syntax.Initializer, value)
			: head;
	}
	
	private static string DescribeConstant(string head, IExpressionNode? initializer, Constant? value)
	{
		var expression = initializer is null ? null : SingleLine(initializer.SourceLocation);
		
		var text = expression is null ? head : $"{head} = {expression}";
		return value is not null && ConstantText.Describe(value) is { } shown && shown != expression
			? $"{text}\n= {shown}"
			: text;
	}
	
	private static string DescribeMember(MemberSymbol member, TypeSymbol type) => member switch
	{
		MethodSymbol method => $"{method.Name}: fun",
		_ => $"{member.Name}: {TypeName(type)}"
	};
	
	private static string DescribeCaseName(TypeSymbol type, EnumCaseSymbol enumCase) =>
		$"{TypeName(type.OriginalDefinition)}.{enumCase.Name}";
	
	private string DescribeCase(TypeSymbol type, EnumCaseSymbol enumCase)
	{
		var node = enumCase.Node;
		var head = DescribeCaseName(type, enumCase) + (node.Payload.IsEmpty
			? ""
			: $"({string.Join(", ", node.Payload.Select(static field => DescribeField(field.Identifier, field)))})");
		
		if ((type as EnumSymbol ?? type.OriginalDefinition as EnumSymbol) is not { } enumType)
			return head;
		
		if (enumType.IsMatch)
			return node.Else is not null ? $"{head} = else"
				: node.Values.IsEmpty ? head
				: $"{head} = {string.Join(", ", node.Values.Select(static value => SingleLine(value.SourceLocation)))}";
		
		if (_caseValues?.Invoke(enumType, enumCase) is not { } value)
			return head;
		
		var shown = value.ToString();
		return node.Value is { } expression && SingleLine(expression.SourceLocation) is var written && written != shown
			? $"{head} = {written}\n= {shown}"
			: $"{head} = {shown}";
	}
	
	private static string DescribeField(FieldSymbol field) =>
		field.Node is { } node ? DescribeField(node.Identifier, node) : field.Name;
	
	private static string DescribeField(Token name, FieldNode node) =>
		SingleLine(node.Type.SourceLocation) is { Length: > 0 } type ? $"{name.Text}: {type}" : name.Text;
	
	private static string SingleLine(SourceLocation location) =>
		string.Join(' ', location.GetText().ToString().Split('\n')
			.Select(static line => line.Trim())
			.Where(static line => line.Length > 0));
	
	private static string DescribeFunction(FunctionInfo info) => DescribeSignature(info, false).Label;
	
	private string DescribeNamed(Symbol symbol, Func<FunctionSymbol, FunctionInfo?>? functions) => symbol switch
	{
		TraitSymbol trait => $"{trait.Name}: trait",
		TypeSymbol type => DescribeType(type),
		ModulePathSymbol module => $"mod {module.Path}",
		FunctionSymbol function when functions?.Invoke(function) is { } info => DescribeFunction(info),
		GlobalSymbol global when _globals?.Invoke(global) is { } info => DescribeGlobal(global, info.Type),
		_ => symbol.Name
	};
	
	public static IEnumerable<int> GetCallParameters(FunctionInfo info) =>
		Enumerable.Range(0, info.Signature.ParameterTypes.Length).Where(i =>
			i >= info.Symbol.Parameters.Length || info.Symbol.Parameters[i].Name != "self");
	
	public static (string Label, ImmutableArray<(int Start, int End)> Parameters) DescribeSignature(FunctionInfo info,
		bool isCall)
	{
		var name = info.Symbol.DeclaredName;
		var (tail, ranges) = DescribeSignatureTail(info, isCall);
		return (name + tail, [..ranges.Select(range => (range.Start + name.Length, range.End + name.Length))]);
	}
	
	private static (string Label, ImmutableArray<(int Start, int End)> Parameters) DescribeSignatureTail(
		FunctionInfo info, bool isCall)
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
		
		var label = new StringBuilder($": {keyword}");
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
				label.Append(DescribeParameter(i < parameters.Length ? parameters[i].WrittenName ?? "_" : "_",
					signature, i));
				
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
			ParameterMode.Imm => "imm ",
			_ => ""
		};
		
		return name == "self" ? $"{mode}self" : $"{mode}{name}: {TypeName(signature.GetDeclaredType(index))}";
	}
	
	private static string TypeName(TypeSymbol type) => DiagnosticReporter.DescribeType(type);
	
	private static string RefPrefix(NamedTypeSymbol type) => type.IsRef ? "ref " : "";
	
	private static string DescribeType(TypeSymbol type) =>
		(type is TraitType or TypeParameterSymbol ? type.Name : TypeName(type)) + DescribeTypeTail(type);
	
	private static string DescribeTypeTail(TypeSymbol type) => type switch
	{
		RecordSymbol { IsUnion: true } record => $": {RefPrefix(record)}union",
		RecordSymbol record => $": {RefPrefix(record)}rec",
		EnumSymbol enumType => $": {RefPrefix(enumType)}enum",
		TraitType => ": trait",
		TypeParameterSymbol { ValueType: { } valueType } => $": const {TypeName(valueType)}",
		_ => ""
	};
}