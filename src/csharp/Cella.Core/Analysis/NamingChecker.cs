using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Analysis;

public sealed class NamingChecker(IReadOnlyDictionary<NamingCategory, NamingPattern> rules)
{
	private const string NodeNamespace = "Cella.Core.Syntax.Nodes";
	
	private readonly Dictionary<Type, PropertyInfo[]> _children = [];
	private readonly List<Diagnostic> _diagnostics = [];
	
	public IReadOnlyList<Diagnostic> Check(FileNode file)
	{
		_diagnostics.Clear();
		foreach (var part in file.ModuleName.Parts)
			CheckName(part, NamingCategory.Modules);
		
		CheckDeclarations(file.Declarations, false);
		return [.._diagnostics];
	}
	
	public IReadOnlyList<Diagnostic> CheckImplTypeParameters(IEnumerable<FileNode> files,
		Func<ImplNode, int, bool> declares)
	{
		_diagnostics.Clear();
		foreach (var impl in files.SelectMany(static file => file.Declarations).OfType<ImplNode>())
			CheckTypeParameters(impl.TypeParameters.Where((_, i) => declares(impl, i)));
		
		return [.._diagnostics];
	}
	
	private void CheckDeclarations(IEnumerable<IDeclarationNode> declarations, bool isTraitImpl)
	{
		foreach (var declaration in declarations)
		{
			switch (declaration)
			{
				case RecordNode record:
					CheckName(record.Identifier, NamingCategory.Types);
					CheckTypeParameters(record.TypeParameters);
					CheckDeclarations(record.Members, false);
					break;
				
				case EnumNode { IsExternal: false } enumNode:
					CheckName(enumNode.Identifier, NamingCategory.Types);
					CheckTypeParameters(enumNode.TypeParameters);
					foreach (var enumCase in enumNode.Cases)
					{
						CheckName(enumCase.Identifier, NamingCategory.EnumCases);
						foreach (var field in enumCase.Payload)
							CheckName(field.Identifier, NamingCategory.Fields);
					}
					
					CheckDeclarations(enumNode.Members, false);
					break;
				
				case TraitNode trait:
					CheckName(trait.Identifier, NamingCategory.Traits);
					CheckTypeParameters(trait.TypeParameters);
					CheckDeclarations(trait.Members, false);
					break;
				
				case ImplNode impl:
					CheckDeclarations(impl.Members, !impl.Traits.IsEmpty);
					break;
				
				case FunctionNode { IsExternal: false } function:
					CheckFunction(function, isTraitImpl);
					break;
				
				case PropertyNode property:
					if (!isTraitImpl)
						CheckName(property.Identifier, NamingCategory.Properties);
					
					foreach (var accessor in property.Accessors)
						CheckFunction(accessor, true);
					
					break;
				
				case FieldNode field:
					CheckName(field.Identifier, NamingCategory.Fields);
					break;
				
				case GlobalNode { IsExternal: false } global:
					CheckName(global.Identifier, global.IsConstant ? NamingCategory.Constants : NamingCategory.Globals);
					if (global.Initializer is { } initializer)
						Walk(initializer);
					
					break;
				
				case ConstructorNode constructor:
					CheckParameters(constructor.Parameters);
					if (constructor.Body is { } body)
						Walk(body);
					
					break;
				
				case DestructorNode destructor:
					Walk(destructor);
					break;
			}
		}
	}
	
	private void CheckFunction(FunctionNode function, bool isTraitImpl)
	{
		if (!isTraitImpl)
		{
			CheckName(function.Identifier, NamingCategory.Functions);
			CheckParameters(function.Parameters);
		}
		
		CheckTypeParameters(function.TypeParameters);
		if (function.Body is { } body)
			Walk(body);
	}
	
	private void CheckParameters(IEnumerable<ParameterNode> parameters)
	{
		foreach (var parameter in parameters)
			CheckName(parameter.Identifier, NamingCategory.Parameters);
	}
	
	private void CheckTypeParameters(IEnumerable<TypeParameterNode> parameters)
	{
		foreach (var parameter in parameters)
			CheckName(parameter.Identifier,
				parameter.ValueType is null ? NamingCategory.TypeParameters : NamingCategory.Parameters);
	}
	
	private void Walk(object root)
	{
		var pending = new Stack<object>([root]);
		while (pending.TryPop(out var node))
		{
			switch (node)
			{
				case VarStatementNode variable:
					CheckName(variable.Identifier,
						variable.IsConstant ? NamingCategory.Constants : NamingCategory.Locals);
					
					break;
				
				case ForStatementNode loop:
					CheckName(loop.Binding, NamingCategory.Locals);
					if (loop.Index is { } index)
						CheckName(index.Name, NamingCategory.Locals);
					
					break;
				
				case PatternNode { Kind: PatternKind.Binding } binding:
					CheckName(binding.CaseName, NamingCategory.Locals);
					break;
				
				case LambdaParameterNode parameter:
					CheckName(parameter.Identifier, NamingCategory.Parameters);
					break;
			}
			
			foreach (var property in GetChildProperties(node.GetType()))
			{
				switch (property.GetValue(node))
				{
					case null or string:
						break;
					
					case IEnumerable items:
						if (IsDefaultArray(items))
							break;
						
						foreach (var item in items)
						{
							if (item is not null && IsNode(item))
								pending.Push(item);
						}
						
						break;
					
					case var child when IsNode(child):
						pending.Push(child);
						break;
				}
			}
		}
	}
	
	private void CheckName(Token name, NamingCategory category)
	{
		if (name.Type != TokenType.Identifier || name.Text == "_" || !rules.TryGetValue(category, out var pattern) ||
		    pattern.Matches(name.Text))
			return;
		
		_diagnostics.Add(new(DiagnosticSeverity.Hint, name.SourceLocation,
			$"'{name.Text}' doesn't match {pattern.Text}")
		{
			Lint = Lints.Naming,
			Fixes = pattern.Suggest(name.Text) is { } suggestion
				? [DiagnosticFix.RenameTo(name.SourceLocation, suggestion)]
				: []
		});
	}
	
	private PropertyInfo[] GetChildProperties(Type type)
	{
		if (!_children.TryGetValue(type, out var properties))
			_children[type] = properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
				.Where(static property => property.GetIndexParameters().Length == 0)
				.ToArray();
		
		return properties;
	}
	
	private static bool IsNode(object value) => value.GetType().Namespace == NodeNamespace;
	
	private static bool IsDefaultArray(object items) =>
		items.GetType() is { IsGenericType: true } type &&
		type.GetGenericTypeDefinition() == typeof(ImmutableArray<>) &&
		type.GetProperty(nameof(ImmutableArray<object>.IsDefault))!.GetValue(items) is true;
}