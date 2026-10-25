using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private readonly HashSet<FunctionSymbol> _resolvingBodies = [];
	
	private ConstantBody? ResolveConstantBody(FunctionSymbol function)
	{
		if (_typePool.GetConstantBody(function) is { } body)
			return body;
		
		if (!function.IsConstant && !IsTraitDefault(function) ||
		    _signatures.FindDeclarationContext(function) is not { } declaration ||
		    !_resolvingBodies.Add(function))
			return null;
		
		var depth = _resolutionContexts.Count;
		try
		{
			_resolutionContexts.Push(CreateFileContext(declaration.File) with
			{
				ContainingType = declaration.ContainingType,
				Trait = declaration.Trait
			});
			
			if (declaration.ImplBlock is { } impl)
				_resolutionContexts.Push(WithImplBlock(CurrentResolutionContext, impl,
					_signatures.GetImplTarget(impl)));
			
			switch (function.Syntax)
			{
				case FunctionNode node:
					ResolveFunction(node, node.Body);
					break;
				
				case ConstructorNode node:
					ResolveFunction(node, node.Body);
					break;
			}
		}
		finally
		{
			while (_resolutionContexts.Count > depth)
				_resolutionContexts.Pop();
			
			_resolvingBodies.Remove(function);
		}
		
		return _typePool.GetConstantBody(function);
	}
	
	private void RegisterConstantBody(ResolvedFunctionNode node, int mark)
	{
		var isChecked = new ConstantBodyChecker(_typePool, Diagnostics).Check(node);
		_typePool.RegisterConstantBody(new(node, isChecked && Diagnostics.CountErrorsSince(mark) == 0));
	}
	
	private void RegisterDefaultBody(ResolvedFunctionNode node, TraitSymbol trait, int mark)
	{
		var checker = new ConstantBodyChecker(_typePool, new DiagnosticList());
		var isChecked = checker.CheckDefault(node, trait);
		_typePool.RegisterConstantBody(new(node, isChecked && Diagnostics.CountErrorsSince(mark) == 0)
		{
			Problem = checker.FirstError
		});
	}
	
	private static bool IsTraitDefault(FunctionSymbol function) =>
		function is { Trait: not null, Syntax: FunctionNode { Body: not null } };
}