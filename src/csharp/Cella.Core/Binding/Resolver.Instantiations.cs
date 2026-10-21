using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public HashSet<LocalVariableSymbol> UsedLocals { get; } = [];
	
	private void MarkUsed(LocalVariableSymbol local)
	{
		while (local.Captured is LocalVariableSymbol captured)
			local = captured;
		
		UsedLocals.Add(local);
	}
	
	private void TrackFunctionUse(FunctionInfo info, IExpressionNode syntax)
	{
		MarkUsed(info.Symbol);
		if ((!_signatures.IsLocal(info.Symbol) || info.File?.Module != CurrentResolutionContext.File.Module) &&
		    _importedFunctions.TryAdd(info.Symbol, info))
			Journal(() => _importedFunctions.Remove(info.Symbol));
		
		if (!info.TypeArguments.IsDefaultOrEmpty)
			AddGenericUse(new(FindGenericCaller(CurrentResolutionContext), info.Symbol, info.TypeArguments,
				syntax.SourceLocation));
		
		if (FindGenericCaller(CurrentResolutionContext) is not { } caller || info.TypeArguments.IsDefaultOrEmpty)
			return;
		
		for (var i = 0; i < info.TypeArguments.Length; i++)
		{
			var argument = info.TypeArguments[i];
			foreach (var parameter in caller.TypeParameters.Where(parameter => Mentions(argument, parameter)))
				AddInstantiation(new((caller, parameter), (info.Symbol, info.Symbol.TypeParameters[i]),
					argument != parameter, syntax.SourceLocation));
		}
	}
	
	private static FunctionSymbol? FindGenericCaller(ResolutionContext context)
	{
		if (context.ContainingFunction is not { Symbol: { TypeParameters.IsEmpty: false } caller })
			return null;
		
		while (caller is { Syntax: LambdaDeclarationNode, ContainingFunction.Symbol: var enclosing })
			caller = enclosing;
		
		return caller;
	}
	
	private void TrackTypeInstance(ResolutionContext context, TypeSymbol type, SourceLocation location)
	{
		if (FindGenericCaller(context) is { } caller)
			RecordInstantiations(caller, caller.TypeParameters, type, location);
	}
	
	private void RecordInstantiations(Symbol owner, ImmutableArray<TypeParameterSymbol> parameters, TypeSymbol type,
		SourceLocation location)
	{
		switch (type)
		{
			case NamedTypeSymbol { Definition.IsGenericDefinition: true } named:
				for (var i = 0; i < named.TypeArguments.Length; i++)
				{
					var argument = named.TypeArguments[i];
					foreach (var parameter in parameters.Where(parameter => Mentions(argument, parameter)))
						AddInstantiation(new((owner, parameter), (named.Definition, named.Definition.TypeParameters[i]),
							argument != parameter, location));
					
					RecordInstantiations(owner, parameters, argument, location);
				}
				
				break;
			
			case PointerType pointer:
				RecordInstantiations(owner, parameters, pointer.BaseType, location);
				break;
			
			case BorrowType borrow:
				RecordInstantiations(owner, parameters, borrow.Target, location);
				break;
			
			case ArrayType array:
				RecordInstantiations(owner, parameters, array.ElementType, location);
				break;
			
			case FunctionType function:
				foreach (var part in function.ParameterTypes.Append(function.ReturnType))
					RecordInstantiations(owner, parameters, part, location);
				
				break;
		}
	}
	
	private void RecordDeclarationInstantiations()
	{
		foreach (var symbol in _symbolTable.DeclarationSymbols.Values)
		{
			using var frame = Own(symbol);
			switch (symbol)
			{
				case NamedTypeSymbol { IsGenericDefinition: true } definition:
					RecordFieldInstantiations(definition);
					break;
				
				case FunctionSymbol { TypeParameters.IsEmpty: false, Syntax: FunctionNode node } function:
					RecordSignatureInstantiations(function, node);
					break;
			}
		}
	}
	
	private void RecordFieldInstantiations(NamedTypeSymbol definition)
	{
		IEnumerable<FieldSymbol> fields = definition switch
		{
			RecordSymbol record => _typePool.GetMembers(record).OfType<FieldSymbol>(),
			EnumSymbol enumType => _typePool.GetPayloadFields(enumType),
			_ => []
		};
		
		foreach (var field in fields)
		{
			if (field.Node is { } node && _typePool.TryGetTypeOfMember(field, out var type))
				RecordInstantiations(definition, definition.TypeParameters, type, node.Type.SourceLocation);
		}
		
		if (_typePool.GetDestructor(definition) is not { Symbol: var destructor })
			return;
		
		foreach (var parameter in definition.TypeParameters.Where(destructor.TypeParameters.Contains))
			AddInstantiation(new((definition, parameter), (destructor, parameter), false, destructor.Definition));
	}
	
	private void RecordSignatureInstantiations(FunctionSymbol function, FunctionNode node)
	{
		var signature = _signatures.GetFunctionInfo(function).Signature;
		var offset = signature.ParameterTypes.Length - node.Parameters.Length;
		for (var i = 0; i < node.Parameters.Length && offset >= 0; i++)
			RecordInstantiations(function, function.TypeParameters, signature.GetDeclaredType(i + offset),
				node.Parameters[i].Type.SourceLocation);
		
		if (node.ReturnType is { } returnType)
			RecordInstantiations(function, function.TypeParameters, signature.ReturnType, returnType.SourceLocation);
	}
	
	private void AddInstantiation(Instantiation instantiation)
	{
		_instantiations.Add(instantiation with { ErrorOwner = CurrentOwner });
		Journal(() => _instantiations.RemoveAt(_instantiations.Count - 1));
	}
	
	private void Journal(Action undo) => journal?.Add(undo);
	
	private Checkpoint OpenCheckpoint()
	{
		journal ??= [];
		openCheckpoints++;
		var redirects = new[] { Diagnostics, CurrentResolutionContext.Diagnostics }
			.Distinct()
			.Select(static list =>
			{
				var captured = new List<Diagnostic>();
				return new Redirection(list, list.Redirect(captured), captured);
			})
			.ToList();
		
		return new(journal.Count, redirects);
	}
	
	private void Commit(Checkpoint checkpoint)
	{
		Close(checkpoint);
		foreach (var redirection in checkpoint.Redirections)
			redirection.List.AddRange(redirection.Captured);
	}
	
	private void Rollback(Checkpoint checkpoint)
	{
		var entries = journal!;
		for (var i = entries.Count - 1; i >= checkpoint.JournalLength; i--)
			entries[i]();
		
		entries.RemoveRange(checkpoint.JournalLength, entries.Count - checkpoint.JournalLength);
		Close(checkpoint);
	}
	
	private void Close(Checkpoint checkpoint)
	{
		foreach (var redirection in Enumerable.Reverse(checkpoint.Redirections))
			redirection.List.Redirect(redirection.Previous);
		
		if (--openCheckpoints == 0)
			journal = null;
	}
	
	private static bool Mentions(TypeSymbol type, TypeParameterSymbol parameter) => type switch
	{
		TypeParameterSymbol other => other == parameter,
		NamedTypeSymbol named => named.TypeArguments.Any(argument => Mentions(argument, parameter)),
		PointerType pointer => Mentions(pointer.BaseType, parameter),
		BorrowType borrow => Mentions(borrow.Target, parameter),
		ArrayType array => Mentions(array.ElementType, parameter),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Any(part => Mentions(part, parameter)),
		_ => false
	};
	
	public void ReportInfiniteInstantiations()
	{
		RecordDeclarationInstantiations();
		var edges = _instantiations.ToLookup(static instantiation => instantiation.From);
		var components = new Dictionary<(Symbol, TypeParameterSymbol), int>();
		var indices = new Dictionary<(Symbol, TypeParameterSymbol), (int Index, int Low)>();
		var stack = new Stack<(Symbol, TypeParameterSymbol)>();
		var count = 0;
		
		foreach (var node in _instantiations.Select(static instantiation => instantiation.From))
		{
			if (!indices.ContainsKey(node))
				Connect(node);
		}
		
		var locations = _instantiations
			.Where(instantiation => instantiation.IsExpansive &&
			                        components.GetValueOrDefault(instantiation.From, -1) ==
			                        components.GetValueOrDefault(instantiation.To, -2))
			.DistinctBy(static instantiation => instantiation.Location);
		
		foreach (var instantiation in locations)
		{
			using var frame = Own(instantiation.ErrorOwner);
			Diagnostics.Add(new(DiagnosticSeverity.Error, instantiation.Location,
				$"Instantiating '{DescribeInstantiated(instantiation.To.Owner)}' never ends"));
		}
		
		return;
		
		void Connect((Symbol, TypeParameterSymbol) node)
		{
			var index = indices.Count;
			indices[node] = (index, index);
			stack.Push(node);
			foreach (var next in edges[node].Select(static instantiation => instantiation.To))
			{
				if (!indices.ContainsKey(next))
				{
					Connect(next);
					indices[node] = (index, Math.Min(indices[node].Low, indices[next].Low));
				}
				else if (stack.Contains(next))
				{
					indices[node] = (index, Math.Min(indices[node].Low, indices[next].Index));
				}
			}
			
			if (indices[node].Low != index)
				return;
			
			var component = count++;
			(Symbol, TypeParameterSymbol) member;
			do
			{
				member = stack.Pop();
				components[member] = component;
			} while (member != node);
		}
	}
}