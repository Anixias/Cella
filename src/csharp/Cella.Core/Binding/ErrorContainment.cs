using Cella.Core.Binding.Nodes;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class ErrorContainment : IDiagnosticGate
{
	private sealed class Group
	{
		public bool IsBroken { get; set; }
	}
	
	public readonly record struct Owner(IDeclarationNode? Declaration, bool IsSilenced);
	
	public readonly struct Scope(ErrorContainment? containment) : IDisposable
	{
		public void Dispose() => containment?.Exit();
	}
	
	private readonly HashSet<IStatementNode> _brokenStatements = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<IDeclarationNode> _brokenDeclarations = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<IDeclarationNode> _syntaxErrors = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<IDeclarationNode> _signatureErrors = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<IDeclarationNode> _resolutionErrors = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<IDeclarationNode, PropertyNode> _accessors = new(ReferenceEqualityComparer.Instance);
	private readonly List<Owner> _frames = [];
	
	private readonly Dictionary<Diagnostic, (IDeclarationNode Declaration, bool IsResolving)> _owners =
		new(ReferenceEqualityComparer.Instance);
	
	private readonly Dictionary<IResolvedDeclarationNode, Group> _groups = [];
	
	public ErrorContainment(IEnumerable<FileNode> files)
	{
		foreach (var file in files)
		{
			_brokenStatements.UnionWith(file.BrokenStatements);
			_brokenDeclarations.UnionWith(file.BrokenDeclarations);
			_syntaxErrors.UnionWith(file.DeclarationsWithSyntaxErrors);
			AddAccessors(file.Declarations);
		}
	}
	
	public bool IsResolving { get; set; }
	
	public Owner Current
	{
		get
		{
			for (var i = _frames.Count - 1; i >= 0; i--)
			{
				if (_frames[i] is { IsSilenced: true } or { Declaration: not null })
					return _frames[i];
			}
			
			return default;
		}
	}
	
	public void Enter(IDeclarationNode? declaration) =>
		_frames.Add(new(declaration is null ? null : Canonical(declaration), false));
	
	public void Enter(Owner owner) => _frames.Add(owner);
	
	public bool Enter(IStatementNode statement)
	{
		if (!_brokenStatements.Contains(statement))
			return false;
		
		_frames.Add(new(null, true));
		return true;
	}
	
	public void Exit() => _frames.RemoveAt(_frames.Count - 1);
	
	public bool HasBrokenSyntax(IDeclarationNode declaration) => _brokenDeclarations.Contains(Canonical(declaration));
	
	public bool Admit(Diagnostic diagnostic)
	{
		if (_owners.ContainsKey(diagnostic))
			return true;
		
		for (var i = _frames.Count - 1; i >= 0; i--)
		{
			var (declaration, isSilenced) = _frames[i];
			if (isSilenced)
				return false;
			
			if (declaration is null)
				continue;
			
			if (_brokenDeclarations.Contains(declaration) || IsResolving && _signatureErrors.Contains(declaration))
				return false;
			
			_owners[diagnostic] = (declaration, IsResolving);
			return true;
		}
		
		return true;
	}
	
	public void Record(Diagnostic diagnostic)
	{
		if (diagnostic.Severity == DiagnosticSeverity.Error && _owners.TryGetValue(diagnostic, out var owner))
			(owner.IsResolving ? _resolutionErrors : _signatureErrors).Add(owner.Declaration);
	}
	
	public void AddDeclarations(IEnumerable<ResolvedFileNode> files)
	{
		foreach (var file in files)
		{
			var declarations = Collect(file.Declarations)
				.Select(static node => (Node: node, Extent: GetExtent(node)))
				.OrderBy(static entry => entry.Extent.Start)
				.ThenByDescending(static entry => entry.Extent.End);
			
			(TextRange Range, Group Group)? outer = null;
			foreach (var (node, extent) in declarations)
			{
				if (outer is not { } current || extent.Start < current.Range.Start || extent.End > current.Range.End)
					outer = (extent, new Group());
				
				outer.Value.Group.IsBroken |= HasErrors(Canonical(node.Syntax));
				_groups[node] = outer.Value.Group;
			}
		}
	}
	
	public void Mark(IResolvedDeclarationNode node)
	{
		if (_groups.TryGetValue(node, out var group))
			group.IsBroken = true;
	}
	
	public bool IsBroken(IResolvedDeclarationNode node) => _groups.TryGetValue(node, out var group) && group.IsBroken;
	
	private void AddAccessors(IEnumerable<IDeclarationNode> declarations)
	{
		foreach (var declaration in declarations)
		{
			switch (declaration)
			{
				case PropertyNode property:
					foreach (var accessor in property.Accessors)
						_accessors[accessor] = property;
					
					break;
				
				case RecordNode record:
					AddAccessors(record.Members);
					break;
				
				case EnumNode enumNode:
					AddAccessors(enumNode.Members);
					break;
				
				case TraitNode trait:
					AddAccessors(trait.Members);
					break;
				
				case ImplNode impl:
					AddAccessors(impl.Members);
					break;
			}
		}
	}
	
	private IDeclarationNode Canonical(IDeclarationNode declaration) =>
		_accessors.TryGetValue(declaration, out var property) ? property : declaration;
	
	private bool HasErrors(IDeclarationNode declaration) =>
		_syntaxErrors.Contains(declaration) || _brokenDeclarations.Contains(declaration) ||
		_signatureErrors.Contains(declaration) || _resolutionErrors.Contains(declaration);
	
	private static IEnumerable<IResolvedDeclarationNode> Collect(IEnumerable<IResolvedDeclarationNode> declarations)
	{
		foreach (var declaration in declarations)
		{
			switch (declaration)
			{
				case ResolvedFunctionNode or ResolvedGlobalNode:
					yield return declaration;
					break;
				
				case ResolvedRecordNode record:
					foreach (var member in Collect(record.Members))
						yield return member;
					
					break;
				
				case ResolvedEnumNode @enum:
					foreach (var member in Collect(@enum.Members))
						yield return member;
					
					break;
				
				case ResolvedTraitNode trait:
					foreach (var member in Collect(trait.Members))
						yield return member;
					
					break;
				
				case ResolvedImplNode impl:
					foreach (var member in Collect(impl.Members))
						yield return member;
					
					break;
			}
		}
	}
	
	private static TextRange GetExtent(IResolvedDeclarationNode node) => node switch
	{
		ResolvedFunctionNode { Syntax: FunctionNode function } => function.Extent.Range,
		ResolvedFunctionNode function => function.Syntax.SourceLocation.Range,
		ResolvedGlobalNode { Syntax: GlobalNode global } => global.Extent.Range,
		ResolvedGlobalNode global => global.Syntax.SourceLocation.Range,
		_ => throw new ArgumentOutOfRangeException(nameof(node))
	};
}