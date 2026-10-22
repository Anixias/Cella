using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public IResolvedDeclarationNode Visit(FieldNode node) => new ResolvedFieldNode(node);
	
	public IResolvedDeclarationNode Visit(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		_importedFunctions.Clear();
		_resolutionContexts.Push(CreateFileContext(file));
		
		var resolvedDeclarations = new List<IResolvedDeclarationNode>(node.Declarations.Length);
		foreach (var declaration in node.Declarations)
			resolvedDeclarations.Add(VisitNode(declaration));
		
		RecordPanicTarget(file, node);
		resolvedDeclarations.AddRange(_lambdas.Where(lambda => lambda.FunctionInfo.File == file));
		_lambdas.RemoveAll(lambda => lambda.FunctionInfo.File == file);
		_resolutionContexts.Pop();
		_extSignatureTypes.Report(Diagnostics);
		ReportTooLargeTypes();
		
		var result = new ResolvedFileNode(file, resolvedDeclarations, _importedFunctions.Values, node);
		_importedFunctions.Clear();
		return result;
	}
	
	private ResolutionContext CreateFileContext(FileSymbol file) => new()
	{
		File = file,
		Imports = _signatures.GetImports(file),
		Modules = _signatures.Modules,
		TypePool = _typePool,
		Diagnostics = Diagnostics,
		ExtSignatureTypes = _extSignatureTypes,
		Containment = Containment,
		EvaluateConstant = EvaluateConstant,
		FindValueParameterUse = FindValueParameterUse,
		MarkUsed = MarkUsed,
		GenericTypes = [],
		RecordUndo = Journal,
		TrackInstance = TrackTypeInstance,
		RecordName = RecordName,
		Probe = Probe
	};
	
	private void ReportTooLargeTypes()
	{
		foreach (var (type, location, owner, errorOwner) in _typePool.SizeChecks)
		{
			if (owner is not null && TypePool.ContainsTypeParameters(type))
			{
				if (!_typePool.GenericSizeChecks.TryGetValue(owner, out var checks))
					_typePool.GenericSizeChecks[owner] = checks = [];
				
				checks.Add((type, location));
			}
			else if (_typePool.IsTooLarge(type, _pointerBitSize))
			{
				using var frame = Own(errorOwner);
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"'{type.Name}' is too large"));
			}
		}
		
		_typePool.SizeChecks.Clear();
	}
	
	public IResolvedDeclarationNode Visit(ConstructorNode node) => ResolveFunction(node, node.Body);
	
	public IResolvedDeclarationNode Visit(DestructorNode node) => ResolveFunction(node, node.Body);
	
	public IResolvedDeclarationNode Visit(FunctionNode node) => ResolveFunction(node, node.Body);
	
	private IEnumerable<IResolvedDeclarationNode> VisitFunctionMember(FunctionNode node)
	{
		if (node.Identifier.Type != TokenType.OpQuestion)
			return [VisitNode(node)];
		
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		return ResolveQuestionDeclaration(function) is { } declaration ? [declaration] : [];
	}
	
	private ResolvedFunctionNode ResolveFunction(IDeclarationNode node, IStatementNode? body)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _signatures.GetFunctionInfo(function);
		
		var resolutionContext = CurrentResolutionContext with
		{
			ContainingFunction = info,
			LocalScope = info.Scope,
			TypeParameters = function.DeclaredTypeParameters
		};
		
		_resolutionContexts.Push(resolutionContext);
		Containment?.Enter(node);
		if (node is FunctionNode { Identifier.Type: TokenType.KeywordPanic } panic)
			ReportPanicDeclaration(panic, info);
		
		var resolvedBody = body is null ? null : InferLocals(() => VisitNode(body));
		Containment?.Exit();
		_resolutionContexts.Pop();
		
		return new ResolvedFunctionNode(info, resolvedBody, node);
	}
	
	public IResolvedDeclarationNode Visit(ExternalFunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _signatures.GetFunctionInfo(function);
		return new ResolvedExternalFunctionNode(info, node);
	}
	
	public IResolvedDeclarationNode Visit(GlobalNode node)
	{
		var global = (GlobalSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _signatures.GetGlobalInfo(global)!.Value;
		if (!global.IsImport && _typePool.ExceedsStaticStorage(info.Type, _pointerBitSize))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"Module variable '{global.Name}' is too large"));
		
		return new ResolvedGlobalNode(info, node);
	}
	
	public IResolvedDeclarationNode Visit(ParameterNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(LambdaDeclarationNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(NativeConstructorNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(NativeFStrPartNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(ConstructorConstraintNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(PropertyNode node) => throw new InvalidOperationException();
	
	private IEnumerable<IResolvedDeclarationNode> VisitMember(IDeclarationNode member) => member switch
	{
		PropertyNode property => property.Accessors.Select(accessor => VisitNode(accessor)),
		FunctionNode function => VisitFunctionMember(function),
		_ => [VisitNode(member)]
	};
	
	public IResolvedDeclarationNode Visit(RecordNode node)
	{
		var record = (RecordSymbol)_symbolTable.DeclarationSymbols[node];
		var members = new List<IResolvedDeclarationNode>();
		
		_resolutionContexts.Push(CurrentResolutionContext with { ContainingType = record });
		foreach (var member in node.Members)
			members.AddRange(VisitMember(member));
		
		_resolutionContexts.Pop();
		return new ResolvedRecordNode(record, members, node);
	}
	
	public IResolvedDeclarationNode Visit(EnumNode node)
	{
		var enumType = (EnumSymbol)_symbolTable.DeclarationSymbols[node];
		var members = new List<IResolvedDeclarationNode>();
		
		_resolutionContexts.Push(CurrentResolutionContext with { ContainingType = enumType });
		foreach (var member in node.Members)
			members.AddRange(VisitMember(member));
		
		_resolutionContexts.Pop();
		return new ResolvedEnumNode(enumType, members, node);
	}
	
	public IResolvedDeclarationNode Visit(TraitNode node)
	{
		var trait = (TraitSymbol)_symbolTable.DeclarationSymbols[node];
		GetDynMembers(trait);
		_resolutionContexts.Push(CurrentResolutionContext with { ContainingType = trait.Self, Trait = trait });
		var members = node.Members.SelectMany(VisitTraitMember).ToList();
		_resolutionContexts.Pop();
		return new ResolvedTraitNode(trait, members, node);
	}
	
	public IResolvedDeclarationNode Visit(ImplNode node)
	{
		var impl = (ImplSymbol)_symbolTable.DeclarationSymbols[node];
		var target = _signatures.GetImplTarget(impl);
		var siblings = GetActiveMemberBlocks(target)
			.Where(block => block != impl && block.TypeParameters.IsEmpty)
			.ToList();
		
		var functions = siblings.SelectMany(block => block.Functions
			.Where(method => IsVisibleExtensionMember(block, method.Function.Visibility))).ToList();
		
		var properties = siblings.SelectMany(block => block.Properties
			.Where(property => IsVisibleExtensionMember(block, property.Visibility))).ToList();
		
		var constants = siblings.SelectMany(block => block.Constants
			.Where(constant => IsVisibleExtensionMember(block, constant.Visibility))).ToList();
		
		_resolutionContexts.Push(CurrentResolutionContext with
		{
			ContainingType = target,
			ImplBlock = impl,
			ResolveSiblingMember = name => ResolutionContext.ResolveStatic(functions, properties, name) ??
			                               constants.FirstOrDefault(constant => constant.Name == name)
		});
		var members = node.Members.SelectMany(VisitImplMember).ToList();
		_resolutionContexts.Pop();
		return new ResolvedImplNode(impl, target, members, node);
	}
	
	private IEnumerable<IResolvedDeclarationNode> VisitTraitMember(IDeclarationNode member) => member switch
	{
		PropertyNode property => property.Accessors.Where(static a => a.Body is not null).Select(a => VisitNode(a)),
		FunctionNode { Body: not null } function => VisitFunctionMember(function),
		_ => []
	};
	
	private IEnumerable<IResolvedDeclarationNode> VisitImplMember(IDeclarationNode member) => member switch
	{
		PropertyNode property => property.Accessors.Select(accessor => VisitNode(accessor)),
		FunctionNode function => VisitFunctionMember(function),
		_ => []
	};
}