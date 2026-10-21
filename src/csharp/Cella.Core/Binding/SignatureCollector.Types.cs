using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void Complete(Symbol symbol)
	{
		switch (symbol)
		{
			case FunctionSymbol function:
				GetFunctionInfo(function);
				break;
			
			case RecordSymbol record:
				CompleteRecord(record);
				break;
			
			case EnumSymbol enumType:
				CompleteEnum(enumType);
				break;
			
			case GlobalSymbol global:
				GetGlobalInfo(global);
				break;
			
			case PropertySymbol property:
				CompleteProperty(property);
				break;
			
			case TraitSymbol trait:
				CompleteTrait(trait);
				break;
			
			case ImplSymbol impl:
				CompleteImpl(impl);
				break;
		}
	}
	
	private void CompleteTrait(TraitSymbol trait)
	{
		if (!_declarations.TryGetValue(trait, out var declaration) || !_completedSymbols.Add(trait))
			return;
		
		var node = (TraitNode)declaration.Node;
		ReportTypeParameters(node.TypeParameters, node.Members);
		CompleteMembers(node.Members, "traits");
		foreach (var constructor in node.Members.OfType<ConstructorNode>().Where(static c => c.Body is not null))
			Diagnostics.Add(constructor.Default is { } defaultKeyword
				? new(DiagnosticSeverity.Error, defaultKeyword.SourceLocation, "Cannot use 'default' in traits")
				: new(DiagnosticSeverity.Error, constructor.Keyword.SourceLocation,
					"Cannot declare constructor bodies in traits"));
		
		foreach (var constructor in node.Members.OfType<ConstructorNode>())
			if (constructor.Visibility is { Type: TokenType.KeywordPvt } visibility)
				Diagnostics.Add(new(DiagnosticSeverity.Error, visibility.SourceLocation,
					"Cannot declare 'pvt' constructors in traits"));
		
		foreach (var function in node.Members.OfType<FunctionNode>().Concat(node.Members.OfType<PropertyNode>()
			         .SelectMany(static property => property.Accessors)))
		{
			var symbol = (FunctionSymbol)_symbolTable.DeclarationSymbols[function];
			if (function.Body is null && symbol.Visibility == Visibility.Private)
				Diagnostics.Add(new(DiagnosticSeverity.Error, function.Identifier.SourceLocation,
					"'pvt' members need a body"));
		}
		
		ReportMembers(trait.Name, trait.Self, node.Members);
		ReportConstructorConflicts(ParsedMembers(node.Members));
	}
	
	private void CompleteImpl(ImplSymbol impl)
	{
		if (!_declarations.TryGetValue(impl, out var declaration) || !_completedSymbols.Add(impl))
			return;
		
		var node = (ImplNode)declaration.Node;
		CompleteMembers(node.Members, "impl blocks");
		var target = GetImplTarget(impl);
		ReportMembers(target.Name, target, node.Members);
	}
	
	private void CompleteMembers(IEnumerable<IDeclarationNode> members, string kind)
	{
		foreach (var member in members)
		{
			using var frame = Own(member);
			var symbol = _symbolTable.DeclarationSymbols[member];
			var context = _declarations[symbol].Context;
			switch (member)
			{
				case FieldNode field:
					Diagnostics.Add(new(DiagnosticSeverity.Error, field.Identifier.SourceLocation,
						$"Cannot declare fields in {kind}"));
					
					break;
				
				case GlobalNode { IsConstant: true } when symbol is GlobalSymbol { Trait: not null } or
					GlobalSymbol { Impl.Node.Traits.IsEmpty: false } or GlobalSymbol { Impl.IsExtension: true }:
					Complete(symbol);
					break;
				
				case GlobalNode global:
					Diagnostics.Add(new(DiagnosticSeverity.Error, global.Identifier.SourceLocation,
						$"Cannot declare static fields in {kind}"));
					
					break;
				
				case DestructorNode destructor:
					Diagnostics.Add(new(DiagnosticSeverity.Error, destructor.Keyword.SourceLocation,
						$"Cannot declare destructors in {kind}"));
					
					break;
				
				case ConstructorNode constructor when context.ImplBlock is not null:
					Diagnostics.Add(new(DiagnosticSeverity.Error, constructor.Keyword.SourceLocation,
						$"Cannot declare constructors in {kind}"));
					
					break;
				
				case ConstructorNode constructor:
					if (!_builder.Functions.ContainsKey((FunctionSymbol)symbol))
						CollectConstructor((FunctionSymbol)symbol, constructor, context);
					
					break;
				
				default:
					Complete(symbol);
					break;
			}
		}
	}
	
	private ImmutableArray<IDeclarationNode> ParsedMembers(ImmutableArray<IDeclarationNode> members) =>
		[..members.Where(member => Containment?.HasBrokenSyntax(member) != true)];
	
	private void ReportMembers(string owner, TypeSymbol type, ImmutableArray<IDeclarationNode> members)
	{
		var parsed = ParsedMembers(members);
		var functions = parsed.OfType<FunctionNode>().ToLookup(IsModeOperator);
		ReportMemberConflicts(owner, [
			..parsed.OfType<PropertyNode>()
				.Select(static p => (p.Identifier, (IDeclarationNode?)p, MemberKind.Property)),
			..functions[false].Select(static f => (f.Identifier, (IDeclarationNode?)f, MemberKind.Function))
		]);
		
		ReportModeOperators(type, [..functions[true]]);
		ReportOperators(type, functions[false]);
	}
	
	private (Token Name, FunctionSymbol? Function)? GetNamedMember(IDeclarationNode member) => member switch
	{
		FieldNode field => (field.Identifier, null),
		GlobalNode global => (global.Identifier, null),
		PropertyNode property => (property.Identifier, null),
		FunctionNode function when !IsModeOperator(function) =>
			(function.Identifier, (FunctionSymbol)_symbolTable.DeclarationSymbols[function]),
		_ => null
	};
	
	private FunctionSignature GetDeclaredSignature(FunctionSymbol function, ImplSymbol? block,
		ImmutableArray<TypeParameterSymbol> declared)
	{
		var signature = GetFunctionInfo(function).Signature;
		return block is null
			? signature
			: _typePool.SubstituteSignature(signature, TypePool.CreateMap(block.TypeParameters, [..declared]));
	}
	
	private void CompleteRecord(RecordSymbol record)
	{
		if (_completedTypes.Contains(record) || !_declarations.TryGetValue(record, out var declaration) ||
		    !Enter(record, false))
			return;
		
		var node = (RecordNode)declaration.Node;
		var destructors = node.Members.OfType<DestructorNode>().Select(static d => d.Keyword);
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(destructors,
			name => $"'{name}' is declared more than once in '{record.Name}'"));
		
		ReportTypeParameters(node.TypeParameters, node.Members);
		
		var storedTypes = new List<(FieldNode Field, ITypeNode Node, TypeSymbol Type)>();
		foreach (var member in node.Members)
		{
			using var frame = Own(member);
			var symbol = _symbolTable.DeclarationSymbols[member];
			var context = _declarations[symbol].Context;
			
			switch (member)
			{
				case FieldNode field:
					var fieldSymbol = (FieldSymbol)symbol;
					if (field is { Visibility: null, WriteVisibility: { } scope } &&
					    record.Node.Visibility is { } typeVisibility &&
					    Visibility.FromKeyword(scope) >= record.Visibility)
						Diagnostics.Add(new(DiagnosticSeverity.Error, scope.SourceLocation,
							$"Cannot use '{scope.Text} set' in '{typeVisibility.Text}' types"));
					
					var fieldType = RejectValueDyn(field.Type, context.ResolveType(field.Type));
					_typePool.RegisterMember(record, fieldSymbol, fieldType);
					if (fieldSymbol.IsRequired)
						_requiredFields.Add((field, fieldType));
					
					ReportHiddenType(field.Type, fieldType, GetEffectiveVisibility(fieldSymbol.Visibility, record),
						field.Identifier.Text);
					
					storedTypes.Add((field, field.Type, fieldType));
					break;
				
				case ConstructorNode constructor:
					CollectConstructor((FunctionSymbol)symbol, constructor, context);
					break;
				
				case DestructorNode destructor when record.IsUnion:
					Diagnostics.Add(new(DiagnosticSeverity.Error, destructor.Keyword.SourceLocation,
						"Cannot declare destructors in unions"));
					
					break;
				
				case DestructorNode destructor:
					if (!node.IsOwn)
						Diagnostics.Add(new(DiagnosticSeverity.Error, destructor.Keyword.SourceLocation,
							"Cannot declare 'drop' in records that aren't 'own'"));
					
					CollectDestructor((FunctionSymbol)symbol, context);
					break;
				
				case GlobalNode:
					break;
				
				default:
					Complete(symbol);
					break;
			}
		}
		
		var parsed = ParsedMembers(node.Members);
		var functions = parsed.OfType<FunctionNode>().ToLookup(IsModeOperator);
		ReportMemberConflicts(record.Name, [
			..parsed.OfType<FieldNode>()
				.Select(static f => (f.Identifier, (IDeclarationNode?)f, MemberKind.Field)),
			..parsed.OfType<GlobalNode>()
				.Select(static g => (g.Identifier, (IDeclarationNode?)g, MemberKind.Field)),
			..parsed.OfType<PropertyNode>()
				.Select(static p => (p.Identifier, (IDeclarationNode?)p, MemberKind.Property)),
			..functions[false].Select(static f => (f.Identifier, (IDeclarationNode?)f, MemberKind.Function))
		]);
		
		ReportConstructorConflicts(parsed);
		ReportModeOperators(record, [..functions[true]]);
		ReportOperators(record, functions[false]);
		_typePool.RegisterRecord(record);
		_completedTypes.Add(record);
		Exit();
		ReportDefaultConstructor(record, node.Members);
		
		if (record.IsUnion)
			ReportUnionMembers(node, _declarations[record].Context);
		else
		{
			foreach (var (member, typeNode, type) in storedTypes)
			{
				if (!node.IsRef && !TypePool.ContainsTypeParameters(type) && _typePool.HoldsBorrows(type))
					ReportOn(member, ReportStoredBorrow(typeNode, node.Identifier, node.Modifiers, "records", "rec"));
				
				if (!node.IsOwn && _typePool.OwnsConcretely(type))
					ReportOn(member, new(DiagnosticSeverity.Error, typeNode.SourceLocation,
						"Cannot store owning values in records that aren't 'own'"));
			}
		}
		
		foreach (var field in node.Members.OfType<GlobalNode>())
			Complete(_symbolTable.DeclarationSymbols[field]);
	}
	
	private void ReportUnionMembers(RecordNode node, ResolutionContext context)
	{
		var fields = node.Members.OfType<FieldNode>().ToList();
		var widest = fields.Select(field => ((FieldSymbol)_symbolTable.DeclarationSymbols[field]).Visibility)
			.DefaultIfEmpty().Max();
		
		foreach (var field in fields)
		{
			using var frame = Own(field);
			var symbol = (FieldSymbol)_symbolTable.DeclarationSymbols[field];
			if (field.WriteVisibility is { } write)
				Diagnostics.Add(new(DiagnosticSeverity.Error, write.SourceLocation,
					"Cannot restrict writes to union members"));
			
			if (symbol.Visibility < widest)
				Diagnostics.Add(new(DiagnosticSeverity.Error,
					(field.Visibility ?? field.Identifier).SourceLocation,
					"Cannot make union members less visible than other members"));
			
			var type = _typePool.GetTypeOfMember(symbol);
			var message = type is InvalidType ? null
				: symbol.IsRequired ? "Cannot declare required fields in unions"
				: _typePool.HoldsBorrows(type) ? "Cannot store borrows in unions"
				: _typePool.NeedsDrop(type) ? "Cannot store values with destructors in unions"
				: !_typePool.HasDefault(type) ? "Cannot store values without defaults in unions"
				: !IsPlainBits(type, context, []) ? $"Cannot store '{type.Name}' values in unions"
				: null;
			
			if (message is not null)
				Diagnostics.Add(new(DiagnosticSeverity.Error,
					symbol.IsRequired ? field.Identifier.SourceLocation : field.Type.SourceLocation, message));
		}
	}
	
	private bool IsPlainBits(TypeSymbol type, ResolutionContext context, HashSet<RecordSymbol> visited) => type switch
	{
		PrimitiveType or PointerType or FunctionType { IsExternal: true } or TypeParameterSymbol => true,
		EnumSymbol enumType => enumType.IsExternal,
		ArrayType array => IsPlainBits(array.ElementType, context, visited),
		RecordSymbol record => !visited.Add(record) || _typePool.GetMembers(record).OfType<FieldSymbol>().All(field =>
			context.CanAccess(record, field.Visibility) && context.CanAccess(record, field.WriteVisibility) &&
			IsPlainBits(_typePool.GetTypeOfMember(field), context, visited)),
		_ => false
	};
	
	private static IEnumerable<Diagnostic>
		ReportDuplicateTypeParameters(ImmutableArray<TypeParameterNode> parameters) =>
		parameters
			.GroupBy(static parameter => parameter.Identifier.Text)
			.Where(static sameName => sameName.Count() > 1)
			.SelectMany(static sameName => sameName.Select(ReportDuplicateTypeParameter));
	
	private void ReportTypeParameters(ImmutableArray<TypeParameterNode> typeParameters,
		IEnumerable<IDeclarationNode> members)
	{
		Diagnostics.AddRange(ReportDuplicateTypeParameters(typeParameters));
		
		var duplicates = typeParameters
			.GroupBy(static p => p.Identifier.Text)
			.Where(static sameName => sameName.Count() > 1)
			.Select(static sameName => sameName.Key)
			.ToHashSet();
		
		var reported = new HashSet<string>();
		foreach (var parameter in members.OfType<FunctionNode>().SelectMany(static f => f.TypeParameters))
		{
			var name = parameter.Identifier.Text;
			if (typeParameters.Where(p => p.Identifier.Text == name).ToList() is not [_, ..] outer)
				continue;
			
			Diagnostics.Add(ReportDuplicateTypeParameter(parameter));
			if (!duplicates.Contains(name) && reported.Add(name))
				Diagnostics.AddRange(outer.Select(ReportDuplicateTypeParameter));
		}
	}
	
	private static Diagnostic ReportDuplicateTypeParameter(TypeParameterNode parameter) => new(DiagnosticSeverity.Error,
		parameter.Identifier.SourceLocation,
		$"{DescribeTypeParameter(parameter, true)} '{parameter.Identifier.Text}' is declared more than once");
	
	private static string DescribeTypeParameter(TypeParameterNode parameter, bool startsSentence) =>
		(parameter.ValueType is null, startsSentence) switch
		{
			(true, true) => "Type parameter",
			(true, false) => "type parameter",
			(false, true) => "Value parameter",
			(false, false) => "value parameter"
		};
	
	private void ReportLayoutCycles()
	{
		foreach (var type in _symbolTable.DeclarationSymbols.Values.OfType<TypeSymbol>())
		{
			using var frame = Own(type);
			foreach (var (field, fieldType) in GetStoredFields(type))
			{
				if (!ContainsType(fieldType, type))
					continue;
				
				var kind = type is EnumSymbol ? "Payload" : "Field";
				Diagnostics.Add(new(DiagnosticSeverity.Error, field.Node!.Type.SourceLocation,
					$"{kind} '{field.Name}' of type '{DiagnosticReporter.DescribeType(fieldType)}' causes a cycle in the memory layout"));
			}
		}
	}
	
	private bool ContainsType(TypeSymbol type, TypeSymbol target)
	{
		var visited = new HashSet<TypeSymbol>();
		var pending = new Stack<TypeSymbol>([type]);
		
		while (pending.TryPop(out var current))
		{
			if (GetStoredType(current) is not { } stored || !visited.Add(stored))
				continue;
			
			if (stored.OriginalDefinition == target)
				return true;
			
			foreach (var (_, fieldType) in GetStoredFields(stored))
				pending.Push(fieldType);
		}
		
		return false;
	}
	
	private IEnumerable<(FieldSymbol Field, TypeSymbol Type)> GetStoredFields(TypeSymbol type)
	{
		IEnumerable<FieldSymbol> fields = type switch
		{
			RecordSymbol record => _typePool.GetMembers(record).OfType<FieldSymbol>(),
			EnumSymbol enumType => _typePool.GetPayloadFields(enumType),
			_ => []
		};
		
		foreach (var field in fields)
			if (_typePool.TryGetTypeOfMember(field, out var fieldType))
				yield return (field, fieldType);
	}
	
	private static TypeSymbol? GetStoredType(TypeSymbol type) => type switch
	{
		RecordSymbol or EnumSymbol => type,
		ArrayType array => GetStoredType(array.ElementType),
		_ => null
	};
}