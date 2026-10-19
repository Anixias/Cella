using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private const string PanicName = "panic";
	
	private readonly Dictionary<FunctionSymbol, FunctionInfo> _panicAdapters = [];
	
	public IResolvedExpressionNode Visit(PanicExpressionNode node)
	{
		var context = CurrentResolutionContext;
		Symbol? symbol;
		if (node.Qualifier is { } qualifier)
		{
			if (context.ResolveModule(qualifier) is not { } module)
				return Error(qualifier, $"'{qualifier.SourceLocation.GetText()}' is not a module", CurrentTargetType);
			
			symbol = context.ResolveMember(module, PanicName);
			if (symbol is null)
				return Error(node, context.ReportUndefinedMember(node.Keyword.SourceLocation, module, PanicName),
					CurrentTargetType);
		}
		else
		{
			symbol = context.Resolve(PanicName);
			if (symbol is null)
				return Error(node, ReportUndefinedSymbol(new VarExpressionNode(node.Keyword), PanicName),
					CurrentTargetType);
		}
		
		if (symbol is not FunctionSymbol target)
			return Error(node, new Diagnostic(DiagnosticSeverity.Error, node.Keyword.SourceLocation,
				$"'{PanicName}' is ambiguous"), CurrentTargetType);
		
		var message = VisitNode(node.Message, null);
		if (IsInvalid(message))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var location = new ResolvedLiteralExpressionNode(NativeSymbols.UntypedString,
			DescribePanicLocation(context.File, node.Keyword.SourceLocation), node);
		
		return (IResolvedExpressionNode?)ResolvePanicCall(target, [message, location], node) ?? Error(node,
			new Diagnostic(DiagnosticSeverity.Error, node.Message.SourceLocation,
				$"'{PanicName}' doesn't accept '{message.Type.Name}'"), CurrentTargetType);
	}
	
	private static string DescribePanicLocation(FileSymbol file, SourceLocation location)
	{
		var (line, column) = location.GetLineColumn();
		return $"{file.Name.Replace('\\', '/')}:{line}:{column}";
	}
	
	private ResolvedFunctionCallExpressionNode? ResolvePanicCall(FunctionSymbol target, IResolvedExpressionNode[] args,
		IExpressionNode syntax)
	{
		var failures = new List<Diagnostic>();
		var candidate = Specialize(new FunctionCallable(GetFunctionInfo(target)), PanicName, args, null,
			syntax.SourceLocation, failures);
		
		if (candidate is null || ResolveCallable([candidate], args, MaterializationMode.Overload) is not
			    { HasResult: true } resolutions)
			return null;
		
		var info = ((FunctionCallable)resolutions[0].Callable).Info;
		TrackFunctionUse(info, syntax);
		return new ResolvedFunctionCallExpressionNode(info, ApplyArgumentResolution(args, resolutions[0]), syntax);
	}
	
	private void ReportPanicDeclaration(FunctionNode node, FunctionInfo info)
	{
		var module = CurrentResolutionContext.File.Module;
		if (module.Files.Sum(static file => file.Symbols.GetValueOrDefault(PanicName)?.Count ?? 0) > 1)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"'{PanicName}' is declared more than once in '{module.Name}'"));
			
			return;
		}
		
		var signature = info.Signature;
		if (signature.ParameterTypes.Any(static type => type is InvalidType) || signature.ReturnType is InvalidType)
			return;
		
		var problem = node switch
		{
			{ Parameters.Length: not 2 } => (node.Identifier.SourceLocation, "'panic' operators need two parameters"),
			_ when signature.ParameterTypes[1] != NativeSymbols.Str =>
				(node.Parameters[1].SourceLocation, "'panic' operators need a 'str' location"),
			_ when signature.ReturnType is not NeverType =>
				(node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
					"'panic' operators must return 'never'"),
			_ when !AcceptsStrings(info) =>
				(node.Parameters[0].SourceLocation, "'panic' operators need a message that accepts 'str'"),
			_ => ((SourceLocation, string)?)null
		};
		
		if (problem is var (location, message))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
	}
	
	private bool AcceptsStrings(FunctionInfo info)
	{
		var syntax = new VarExpressionNode(new Token(TokenType.Identifier, info.Symbol.Definition, PanicName));
		IResolvedExpressionNode[] args =
		[
			new ResolvedVarExpressionNode(new LocalVariableSymbol(syntax.Identifier, NativeSymbols.Str, false),
				NativeSymbols.Str, syntax),
			new ResolvedVarExpressionNode(new LocalVariableSymbol(syntax.Identifier, NativeSymbols.Str, false),
				NativeSymbols.Str, syntax)
		];
		
		var checkpoint = OpenCheckpoint();
		var call = ResolvePanicCall(info.Symbol, args, syntax);
		Rollback(checkpoint);
		return call is not null;
	}
	
	private void RecordPanicTarget(FileSymbol file, FileNode node)
	{
		switch (CurrentResolutionContext.Resolve(PanicName))
		{
			case FunctionSymbol target:
				_signatures.AddPanicTarget(file.FullPath, target);
				break;
			
			case AmbiguousSymbol:
				ReportAmbiguousPanic(node);
				break;
		}
	}
	
	private void ReportAmbiguousPanic(FileNode node)
	{
		var context = CurrentResolutionContext;
		var imports = node.Imports
			.Where(import => import.Import is FullImport &&
			                 context.ResolveMembers(_signatures.Modules.Root, import.ModuleName.Parts, 0) is
				                 ModulePathSymbol module &&
			                 module.Members.GetValueOrDefault(PanicName)?.Any(context.IsVisible) == true)
			.ToList();
		
		foreach (var import in imports)
			Diagnostics.Add(new(DiagnosticSeverity.Error, import.ModuleName.Parts[^1].SourceLocation,
				$"'{PanicName}' is ambiguous"));
	}
	
	public (ImmutableArray<ResolvedFileNode> Files, IReadOnlyDictionary<string, FunctionInfo> Adapters)
		AddPanicAdapters(ImmutableArray<ResolvedFileNode> files)
	{
		var adapters = new Dictionary<string, FunctionInfo>();
		if (files.IsEmpty)
			return (files, adapters);
		
		var host = files[0];
		_resolutionContexts.Push(CreateFileContext(host.Symbol));
		var created = new List<IResolvedDeclarationNode>();
		foreach (var (path, target) in _signatures.PanicTargets)
		{
			if (!_panicAdapters.TryGetValue(target, out var adapter))
			{
				if (CreatePanicAdapter(target) is not var (info, node))
					continue;
				
				_panicAdapters[target] = adapter = info;
				created.Add(node);
			}
			
			adapters[path] = adapter;
		}
		
		_resolutionContexts.Pop();
		if (created.Count == 0)
			return (files, adapters);
		
		var updated = new ResolvedFileNode(host.Symbol, [..host.Declarations, ..created], host.ImportedFunctions,
			host.Syntax);
		
		return ([updated, ..files[1..]], adapters);
	}
	
	private (FunctionInfo Info, ResolvedFunctionNode Node)? CreatePanicAdapter(FunctionSymbol target)
	{
		var context = CurrentResolutionContext;
		var location = target.Definition;
		var message = new Token(TokenType.Identifier, location, "message");
		var place = new Token(TokenType.Identifier, location, "location");
		ParameterSymbol[] parameters = [new(message), new(place)];
		var symbol = new FunctionSymbol("$panic", target.Syntax, Visibility.Project, null, parameters,
			FunctionKind.Free);
		
		var scope = new Scope();
		foreach (var parameter in parameters)
			scope.Define(parameter);
		
		var signature = new FunctionSignature([NativeSymbols.Str, NativeSymbols.Str], NativeSymbols.Never);
		symbol.QualifiedName = context.Qualify(symbol);
		var mangledName = Mangling.MangleAdapter(context.Modules?.AssemblyName, "panic",
			_signatures.GetFunctionInfo(target).MangledName);
		
		var info = new FunctionInfo(mangledName, symbol, signature, scope, null, context.File);
		var syntax = new VarExpressionNode(message);
		IResolvedExpressionNode[] args =
		[
			new ResolvedVarExpressionNode(parameters[0], NativeSymbols.Str, syntax),
			new ResolvedVarExpressionNode(parameters[1], NativeSymbols.Str, new VarExpressionNode(place))
		];
		
		if (ResolvePanicCall(target, args, syntax) is not { } call)
			return null;
		
		_signatures.AddLambda(info);
		var body = new ResolvedExpressionStatementNode(call, new ExpressionStatementNode(syntax));
		return (info, new ResolvedFunctionNode(info, body, target.Syntax));
	}
}