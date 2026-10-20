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
			symbol = context.Resolve(PanicName);
		
		var functions = GetFunctions(symbol);
		if (symbol is not null && functions.Length == 0)
			return ReportAmbiguousPanic(node);
		
		if (node.Message is not { } written)
		{
			if (GetPanicForm(functions, false) is not [_, ..] forms)
				return new ResolvedTrapExpressionNode(node);
			
			if (ResolvePanicCall(forms, [CreatePanicLocation(node)], node, out var isAmbiguous) is { } located)
				return located;
			
			return RecordFailedPanic(node, forms, isAmbiguous
				? ReportAmbiguousPanic(node)
				: new ResolvedInvalidExpressionNode(node, CurrentTargetType));
		}
		
		if (GetPanicForm(functions, true) is not [_, ..] messageForms)
		{
			VisitNode(written, null);
			return RecordFailedPanic(node, functions, Error(node, new Diagnostic(DiagnosticSeverity.Error,
				written.SourceLocation, $"'{PanicName}' doesn't accept a message"), CurrentTargetType));
		}
		
		var message = VisitNode(written, null);
		if (IsInvalid(message))
			return RecordFailedPanic(node, messageForms, new ResolvedInvalidExpressionNode(node, CurrentTargetType));
		
		if (ResolvePanicCall(messageForms, [message, CreatePanicLocation(node)], node, out var ambiguous) is { } call)
			return call;
		
		return RecordFailedPanic(node, messageForms, ambiguous
			? ReportAmbiguousPanic(node)
			: Error(node, new Diagnostic(DiagnosticSeverity.Error, written.SourceLocation,
				$"'{PanicName}' doesn't accept '{message.Type.Name}'"), CurrentTargetType));
	}
	
	private ResolvedInvalidExpressionNode RecordFailedPanic(PanicExpressionNode node, FunctionSymbol[] forms,
		ResolvedInvalidExpressionNode result)
	{
		if (RecordName is { } record)
		{
			foreach (var form in forms)
				record(node.Keyword, form);
		}
		
		return result;
	}
	
	private ResolvedInvalidExpressionNode ReportAmbiguousPanic(PanicExpressionNode node) => Error(node,
		new Diagnostic(DiagnosticSeverity.Error, node.Keyword.SourceLocation, $"'{PanicName}' is ambiguous"),
		CurrentTargetType);
	
	private static ResolvedLiteralExpressionNode CreatePanicLocation(PanicExpressionNode node) =>
		new(NativeSymbols.UntypedString, node.Keyword.SourceLocation.DescribeStart(), node);
	
	private static FunctionSymbol[] GetPanicForm(FunctionSymbol[] functions, bool takesMessage) =>
	[
		..functions.Where(function => function.Parameters.Length == (takesMessage ? 2 : 1))
	];
	
	private ResolvedFunctionCallExpressionNode? ResolvePanicCall(IEnumerable<FunctionSymbol> targets,
		IResolvedExpressionNode[] args, IExpressionNode syntax, out bool isAmbiguous)
	{
		var candidates = targets
			.Select(target => Specialize(new FunctionCallable(GetFunctionInfo(target)), PanicName, args, null,
				syntax.SourceLocation, []))
			.OfType<ICallable>()
			.ToArray();
		
		var resolutions = ResolveCallable(candidates, args, MaterializationMode.Overload);
		isAmbiguous = resolutions.IsAmbiguous;
		if (!resolutions.HasResult || isAmbiguous)
			return null;
		
		var info = ((FunctionCallable)resolutions[0].Callable).Info;
		TrackFunctionUse(info, syntax);
		return new ResolvedFunctionCallExpressionNode(info, ApplyArgumentResolution(args, resolutions[0]), syntax);
	}
	
	private void ReportPanicDeclaration(FunctionNode node, FunctionInfo info)
	{
		var signature = info.Signature;
		if (signature.ParameterTypes.Any(static type => type is InvalidType) || signature.ReturnType is InvalidType)
			return;
		
		var problem = node switch
		{
			{ Parameters.Length: not (1 or 2) } =>
				(node.Identifier.SourceLocation, "'panic' operators need one or two parameters"),
			_ when signature.ParameterTypes[^1] != NativeSymbols.Str =>
				(node.Parameters[^1].SourceLocation, "'panic' operators need a 'str' location"),
			_ when signature.ReturnType is not NeverType =>
				(node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
					"'panic' operators must return 'never'"),
			{ Parameters.Length: 2 } when !AcceptsStrings(info) =>
				(node.Parameters[0].SourceLocation, "'panic' operators need a message that accepts 'str'"),
			_ => ((SourceLocation, string)?)null
		};
		
		if (problem is var (location, message))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
	}
	
	private bool AcceptsStrings(FunctionInfo info) =>
		FindPanicTarget([info.Symbol], true, info.Symbol.Definition, out _) is not null;
	
	private FunctionSymbol? FindPanicTarget(FunctionSymbol[] functions, bool takesMessage, SourceLocation location,
		out bool isAmbiguous)
	{
		var syntax = new VarExpressionNode(new Token(TokenType.Identifier, location, PanicName));
		var argument = new ResolvedVarExpressionNode(
			new LocalVariableSymbol(syntax.Identifier, NativeSymbols.Str, false), NativeSymbols.Str, syntax);
		
		var checkpoint = OpenCheckpoint();
		var call = ResolvePanicCall(GetPanicForm(functions, takesMessage),
			takesMessage ? [argument, argument] : [argument],
			syntax, out isAmbiguous);
		
		Rollback(checkpoint);
		return call?.Function.Symbol;
	}
	
	private readonly HashSet<ImportExpression> _ambiguousPanicImports = [];
	
	private void RecordPanicTarget(FileSymbol file, FileNode node)
	{
		var symbol = CurrentResolutionContext.Resolve(PanicName);
		var functions = GetFunctions(symbol);
		if (symbol is not null && functions.Length == 0)
		{
			ReportAmbiguousPanic(node);
			return;
		}
		
		foreach (var takesMessage in (bool[])[true, false])
		{
			if (FindPanicTarget(functions, takesMessage, node.SourceLocation, out var isAmbiguous) is { } target)
			{
				_signatures.AddPanicTarget(file.FullPath, target);
				return;
			}
			
			if (isAmbiguous)
			{
				ReportAmbiguousPanic(node);
				return;
			}
		}
	}
	
	private void ReportAmbiguousPanic(FileNode node)
	{
		var context = CurrentResolutionContext;
		
		bool ImportsPanic(ImportExpression import) => import.Import is FullImport &&
		                                              context.ResolveMembers(_signatures.Modules.Root,
			                                              import.ModuleName.Parts, 0) is ModulePathSymbol module &&
		                                              module.Members.GetValueOrDefault(PanicName)
			                                              ?.Any(context.IsVisible) ==
		                                              true;
		
		var imports = ProjectImports.Where(ImportsPanic)
			.Where(_ambiguousPanicImports.Add)
			.Concat(node.Imports.Where(ImportsPanic))
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
			_signatures.GetFunctionInfo(target).MangledName ?? target.Name);
		
		var info = new FunctionInfo(mangledName, symbol, signature, scope, null, context.File);
		var syntax = new VarExpressionNode(message);
		var located = new ResolvedVarExpressionNode(parameters[1], NativeSymbols.Str, new VarExpressionNode(place));
		IResolvedExpressionNode[] args = target.Parameters.Length == 2
			? [new ResolvedVarExpressionNode(parameters[0], NativeSymbols.Str, syntax), located]
			: [located];
		
		if (ResolvePanicCall([target], args, syntax, out _) is not { } call)
			return null;
		
		_signatures.AddLambda(info);
		var body = new ResolvedExpressionStatementNode(call, new ExpressionStatementNode(syntax));
		return (info, new ResolvedFunctionNode(info, body, target.Syntax));
	}
}