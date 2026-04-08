using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class TypeParser(ImmutableArray<Token> tokens) : BaseParser<ITypeNode>(tokens)
{
	public override ITypeNode Parse(ref int index) => ParseType(ref index);
	
	private ITypeNode ParseType(ref int index)
	{
		// TEMP Should emit an erroneous node instead
		if (!Match(ref index, out var identifier, TokenType.Identifier))
			throw new InvalidOperationException();
		
		var startIndex = index;
		if (!Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			return new IdentifierTypeNode(identifier);
		
		// Disallow the open bracket being on another line from the identifier; backtrack to unconsume open bracket
		if (openBracket.Line != identifier.Line)
		{
			index = startIndex;
			return new IdentifierTypeNode(identifier);
		}
		
		var typeParameters = new List<ITypeNode> { ParseType(ref index) };
		while (Match(ref index, TokenType.OpComma))
			typeParameters.Add(ParseType(ref index));
		
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw new InvalidOperationException();
		
		var (source, range) = identifier.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		
		return new GenericTypeNode(new(source, range), identifier, typeParameters);
	}
}