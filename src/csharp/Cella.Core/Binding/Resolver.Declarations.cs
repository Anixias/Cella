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
		_copiedParameters.Report(Diagnostics);
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
		CopiedParameters = _copiedParameters,
		EvaluateConstant = EvaluateConstant,
		GenericTypes = [],
		TrackInstance = TrackTypeInstance,
		RecordName = RecordName,
		Probe = Probe
	};
	
	private void ReportTooLargeTypes()
	{
		foreach (var (type, location, owner) in _typePool.SizeChecks)
		{
			if (owner is not null && TypePool.ContainsTypeParameters(type))
			{
				if (!_typePool.GenericSizeChecks.TryGetValue(owner, out var checks))
					_typePool.GenericSizeChecks[owner] = checks = [];
				
				checks.Add((type, location));
			}
			else if (_sizeCheckLocations.Add(location) && _typePool.IsTooLarge(type, _pointerBitSize))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"'{type.Name}' is too large"));
		}
		
		_typePool.SizeChecks.Clear();
	}
	
	public IResolvedDeclarationNode Visit(ConstructorNode node) => ResolveFunction(node, node.Body!);
	
	public IResolvedDeclarationNode Visit(DestructorNode node) => ResolveFunction(node, node.Body!);
	
	public IResolvedDeclarationNode Visit(FunctionNode node) => ResolveFunction(node, node.Body!);
	
	private IEnumerable<IResolvedDeclarationNode> VisitFunctionMember(FunctionNode node)
	{
		if (node.Identifier.Type != TokenType.OpQuestion)
			return [VisitNode(node)];
		
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		if (_signatures.FindInlineContext(function) is { } declaration && FindQuestionBody(function) is { } body)
			IsSoundQuestionBody(function, declaration, body);
		
		return [];
	}
	
	private ResolvedFunctionNode ResolveFunction(IDeclarationNode node, IStatementNode body)
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
		if (node is FunctionNode { Identifier.Type: TokenType.KeywordPanic } panic)
			ReportPanicDeclaration(panic, info);
		
		var extent = node is FunctionNode declared ? declared.Extent : node.SourceLocation;
		var resolvedBody = Containment?.IsInBrokenRegion(extent) == true ? null : InferLocals(() => VisitNode(body));
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
		return new ResolvedGlobalNode(_signatures.GetGlobalInfo(global)!.Value, node);
	}
	
	public IResolvedDeclarationNode Visit(ParameterNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(LambdaDeclarationNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(NativeConstructorNode node) => throw new InvalidOperationException();
	
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
		var members = node.Members.SelectMany(VisitBodyMember).ToList();
		_resolutionContexts.Pop();
		return new ResolvedTraitNode(trait, members, node);
	}
	
	public IResolvedDeclarationNode Visit(ImplNode node)
	{
		var impl = (ImplSymbol)_symbolTable.DeclarationSymbols[node];
		var target = _signatures.GetImplTarget(impl);
		_resolutionContexts.Push(CurrentResolutionContext with { ContainingType = target, ImplBlock = impl });
		var members = node.Members.SelectMany(VisitBodyMember).ToList();
		_resolutionContexts.Pop();
		return new ResolvedImplNode(target, members, node);
	}
	
	private IEnumerable<IResolvedDeclarationNode> VisitBodyMember(IDeclarationNode member) => member switch
	{
		PropertyNode property => property.Accessors.Where(static a => a.Body is not null).Select(a => VisitNode(a)),
		FunctionNode { Body: not null } function => VisitFunctionMember(function),
		_ => []
	};
}