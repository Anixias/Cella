using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed record AtomicRequirement
(
	TypeParameterSymbol Parameter,
	BinaryOperation Operation,
	string Operator,
	SourceLocation Location
);