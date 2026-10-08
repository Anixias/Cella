using System.Collections.Immutable;

namespace Cella.Core.Text;

public sealed record InterpolatedString(ImmutableArray<string> Segments, ImmutableArray<InterpolationHole> Holes);

public readonly record struct InterpolationHole
(
	ImmutableArray<Token> Tokens,
	SourceLocation Close,
	string Spec,
	SourceLocation SpecLocation
);