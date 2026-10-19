using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Analysis;

public sealed class PassSkipper(LoopFollower follower, MovePaths paths, ShapeLayout layout, ValueEvaluator values)
{
	private readonly LoopFollower _follower = follower;
	private readonly MovePaths _paths = paths;
	private readonly ValueEvaluator _values = values;
	private readonly Dictionary<Loop, List<BasicBlock>?> _passes = [];
	
	public (KnownValues Known, InitState State)? Skip(Loop loop, KnownValues entry, InitState entryState,
		KnownValues next, InitState nextState)
	{
		if (GetPass(loop) is not { } pass || entry.Entries.Count != next.Entries.Count)
			return null;
		
		var changing = entry.Entries
			.Where(e => next.Get(e.Symbol) is not { } after || !KnownValues.Same(after, e.Value))
			.Select(static e => e.Symbol)
			.ToHashSet();
		
		if (changing.Count == 0 || new Scan(this, loop, pass, entry, changing).Run() is not { } scan)
			return null;
		
		if (Apply(scan, entryState, 1) is not { } single ||
		    single.Any(entry => !entry.Value.Equals(nextState.GetElements(entry.Key))) ||
		    Apply(scan, entryState, scan.Count) is not { } arrays ||
		    !entryState.IsSameOutside(nextState, arrays.Keys))
			return null;
		
		var known = entry;
		foreach (var (symbol, step, wraps) in scan.Steps)
		{
			if (entry.Get(symbol) is not IntegerConstant value ||
			    _values.Advance(value, step * scan.Count, wraps) is not { } advanced)
				return null;
			
			known = known.With(symbol, advanced);
		}
		
		var state = nextState.Copy();
		foreach (var (array, shape) in arrays)
			state.SetElements(array, shape);
		
		return (known, state);
	}
	
	private Dictionary<MovePath, Shape>? Apply(Scan scan, InitState entryState, BigInteger passes)
	{
		var arrays = new Dictionary<MovePath, Shape>();
		foreach (var group in scan.Groups)
		{
			if (!arrays.TryGetValue(group.Array, out var arrayShape))
				arrayShape = entryState.GetElements(group.Array);
			
			if (Advance(group, arrayShape, scan, passes) is not { } advanced)
				return null;
			
			arrays[group.Array] = advanced;
		}
		
		return arrays;
	}
	
	private Shape? Advance(Group group, Shape arrayShape, Scan scan, BigInteger passes)
	{
		if (layout.GetPartType(group.Array.Type, group.Prefix) is not ArrayType array ||
		    ShapeLayout.GetLength(array) is not { } length)
			return null;
		
		var part = layout.Get(arrayShape, group.Array.Type, group.Prefix, out var reached);
		if (reached != group.Prefix.Length)
			return null;
		
		var elements = ElementsShape.Expand(part, length);
		if (group.Touches.Any(touch => scan.GetRange(touch.Offset, 0, passes) is var (low, high) &&
		                               (low < 0 || high > length)))
			return null;
		
		var writers = group.Touches.Where(static touch => touch.Effect is not null).ToList();
		foreach (var observer in group.Touches.Where(static touch => touch.Observes))
		{
			var first = Observe(scan, observer, writers, elements, 0);
			var lag = writers.Select(writer => scan.FindLag(observer, writer) ?? 0).DefaultIfEmpty(0).Max();
			var settled = BigInteger.Min(passes, lag + 1);
			for (var pass = BigInteger.One; pass < settled; pass++)
			{
				if (!Observe(scan, observer, writers, elements, pass).Equals(first))
					return null;
			}
			
			if (settled < passes &&
			    (FindWriter(scan, observer, writers, passes) is { } writer
				    ? !writer.Effect!.Equals(first)
				    : scan.GetRange(observer.Offset, settled, passes) is var (low, high) &&
				      !elements.IsUniform(low, high, BigInteger.Abs(scan.Step), first)))
				return null;
		}
		
		Shape result = elements;
		foreach (var writer in writers.OrderByDescending(writer => writer.Offset * scan.Step.Sign)
			         .ThenBy(static writer => writer.Order))
		{
			var (low, high) = scan.GetRange(writer.Offset, 0, passes);
			result = ElementsShape.Expand(result, length)
				.SetStrided(low, high, BigInteger.Abs(scan.Step), writer.Effect!);
		}
		
		return Place(arrayShape, group.Array.Type, group.Prefix, 0, result);
	}
	
	private static Shape Observe(Scan scan, Touch observer, List<Touch> writers, ElementsShape elements,
		BigInteger pass) =>
		FindWriter(scan, observer, writers, pass)?.Effect ?? elements.Get(scan.GetElement(pass, observer.Offset));
	
	private static Touch? FindWriter(Scan scan, Touch observer, List<Touch> writers, BigInteger passed) =>
		writers.Where(writer => writer.Offset == observer.Offset && writer.Order < observer.Order)
			.MaxBy(static writer => writer.Order) ??
		writers.Where(writer => scan.FindLag(observer, writer) is { } lag && lag <= passed)
			.OrderBy(writer => scan.FindLag(observer, writer))
			.ThenByDescending(static writer => writer.Order)
			.FirstOrDefault();
	
	private Shape Place(Shape shape, TypeSymbol type, Projection[] prefix, int index, Shape part) =>
		index == prefix.Length
			? part
			: layout.Update(shape, type, prefix[index],
				(inner, innerType) => Place(inner, innerType, prefix, index + 1, part));
	
	private List<BasicBlock>? GetPass(Loop loop)
	{
		if (_passes.TryGetValue(loop, out var cached))
			return cached;
		
		List<BasicBlock>? pass = null;
		if (loop.Head.Terminator is ConditionalBranchTerminator { IsErasedCall: false } &&
		    CfgUtils.GetSuccessors(loop.Head) is [var first, var second] &&
		    loop.Body.Contains(first) != loop.Body.Contains(second))
		{
			pass = [loop.Head];
			var current = loop.Body.Contains(first) ? first : second;
			while (current != loop.Head && pass.Count <= loop.Body.Count && loop.Body.Contains(current) &&
			       current.Terminator is BranchTerminator or ConditionalBranchTerminator &&
			       CfgUtils.GetSuccessors(current) is [var successor])
			{
				pass.Add(current);
				current = successor;
			}
			
			if (current != loop.Head || pass.Count != loop.Body.Count)
				pass = null;
		}
		
		return _passes[loop] = pass;
	}
	
	private sealed record Touch(int Order, BigInteger Offset, Shape? Effect, bool Observes);
	
	private sealed class Group(MovePath array, Projection[] prefix)
	{
		public MovePath Array { get; } = array;
		public Projection[] Prefix { get; } = prefix;
		public List<Touch> Touches { get; } = [];
	}
	
	private sealed class Scan
	(
		PassSkipper skipper,
		Loop loop,
		List<BasicBlock> pass,
		KnownValues entry,
		HashSet<LocalVariableSymbol> changing
	)
	{
		private readonly HashSet<LocalVariableSymbol> _varying = [..changing];
		private readonly Dictionary<LocalVariableSymbol, BigInteger> _offsets = [];
		private KnownValues known = entry;
		private bool rejected;
		private int order;
		
		public List<Group> Groups { get; } = [];
		public List<(LocalVariableSymbol Symbol, BigInteger Step, bool Wraps)> Steps { get; } = [];
		public BigInteger Start { get; private set; }
		public BigInteger Step { get; private set; }
		public BigInteger Count { get; private set; }
		
		public BigInteger GetElement(BigInteger pass, BigInteger offset) => Start + Step * pass + offset;
		
		public (BigInteger Low, BigInteger High) GetRange(BigInteger offset, BigInteger firstPass, BigInteger passes)
		{
			var (first, last) = (GetElement(firstPass, offset), GetElement(passes - 1, offset));
			return (BigInteger.Min(first, last), BigInteger.Max(first, last) + 1);
		}
		
		public BigInteger? FindLag(Touch observer, Touch writer)
		{
			var distance = writer.Offset - observer.Offset;
			return distance % Step == 0 && distance / Step > 0 ? distance / Step : null;
		}
		
		public Scan? Run()
		{
			foreach (var instruction in pass.SelectMany(static block => block.Instructions))
			{
				if (instruction is LocalVarInstruction local)
					_varying.Add(local.Symbol);
			}
			
			if (!FindSteps() || !FindInduction())
				return null;
			
			foreach (var block in pass)
			{
				foreach (var instruction in block.Instructions)
				{
					Classify(skipper._follower.Linearize(instruction, known));
					known = skipper._follower.Advance(instruction, known);
					Track(instruction);
				}
				
				Classify(skipper._follower.Linearize(block.Terminator, known));
				if (block == loop.Head && !FindCount(block))
					return null;
			}
			
			return rejected || Count < 3 ? null : this;
		}
		
		private bool FindSteps()
		{
			foreach (var symbol in changing)
			{
				var assignments = pass.SelectMany(static block => block.Instructions)
					.Where(instruction => instruction is ExpressionInstruction
					{
						Value: AssignValue { Left: VariableValue { Variable.Symbol: var target } }
					} && target == symbol)
					.ToList();
				
				if (assignments is not [ExpressionInstruction { Value: AssignValue { Right: BinOpValue update } }] ||
				    update.Left is not VariableValue { Variable.Symbol: var source } || source != symbol ||
				    GetConstant(update.Right) is not { } amount)
					return false;
				
				switch (update.Op)
				{
					case BinaryOperation.Addition or BinaryOperation.WrappingAddition:
						Steps.Add((symbol, amount, update.Op == BinaryOperation.WrappingAddition));
						break;
					
					case BinaryOperation.Subtraction or BinaryOperation.WrappingSubtraction:
						Steps.Add((symbol, -amount, update.Op == BinaryOperation.WrappingSubtraction));
						break;
					
					default:
						return false;
				}
			}
			
			return true;
		}
		
		private bool FindInduction()
		{
			if (loop.Head.Terminator is not ConditionalBranchTerminator { Condition: var condition })
				return false;
			
			var mentioned = Steps.Where(step => !step.Step.IsZero && Mentions(condition, step.Symbol)).ToList();
			if (mentioned is not [var step] || known.Get(step.Symbol) is not IntegerConstant value)
				return false;
			
			Start = value.Value;
			Step = step.Step;
			_offsets[step.Symbol] = BigInteger.Zero;
			return true;
		}
		
		private bool FindCount(BasicBlock head)
		{
			if (head.Terminator is not ConditionalBranchTerminator { Condition: BinOpValue condition } branch)
				return false;
			
			var operation = loop.Body.Contains(branch.TrueTarget) ? condition.Op : Negate(condition.Op);
			var (offset, limit) = (GetOffset(condition.Left), GetConstant(condition.Right));
			if (offset is null || limit is null)
			{
				(offset, limit) = (GetOffset(condition.Right), GetConstant(condition.Left));
				operation = Swap(operation);
			}
			
			if (offset is null || limit is null)
				return false;
			
			var distance = limit.Value - offset.Value - Start;
			var forward = Step.Sign > 0;
			Count = operation switch
			{
				BinaryOperation.Less when forward => CountUntil(distance, Step, false),
				BinaryOperation.Greater when !forward => CountUntil(distance, Step, false),
				BinaryOperation.LessEqual when forward => CountUntil(distance, Step, true),
				BinaryOperation.GreaterEqual when !forward => CountUntil(distance, Step, true),
				BinaryOperation.NotEqual when distance % Step == 0 => distance / Step,
				_ => BigInteger.Zero
			};
			
			return Count > 0;
		}
		
		private static BigInteger CountUntil(BigInteger distance, BigInteger step, bool isInclusive)
		{
			if (distance.Sign != step.Sign && !distance.IsZero)
				return BigInteger.Zero;
			
			var (whole, rest) = BigInteger.DivRem(BigInteger.Abs(distance), BigInteger.Abs(step));
			return isInclusive ? whole + 1 : rest.IsZero ? whole : whole + 1;
		}
		
		private static BinaryOperation Negate(BinaryOperation operation) => operation switch
		{
			BinaryOperation.Less => BinaryOperation.GreaterEqual,
			BinaryOperation.LessEqual => BinaryOperation.Greater,
			BinaryOperation.Greater => BinaryOperation.LessEqual,
			BinaryOperation.GreaterEqual => BinaryOperation.Less,
			BinaryOperation.Equal => BinaryOperation.NotEqual,
			BinaryOperation.NotEqual => BinaryOperation.Equal,
			_ => operation
		};
		
		private static BinaryOperation Swap(BinaryOperation operation) => operation switch
		{
			BinaryOperation.Less => BinaryOperation.Greater,
			BinaryOperation.LessEqual => BinaryOperation.GreaterEqual,
			BinaryOperation.Greater => BinaryOperation.Less,
			BinaryOperation.GreaterEqual => BinaryOperation.LessEqual,
			_ => operation
		};
		
		private void Track(IInstruction instruction)
		{
			switch (instruction)
			{
				case LocalVarInstruction local when GetOffset(local.Initializer) is { } offset:
					_offsets[local.Symbol] = offset;
					break;
				
				case LocalVarInstruction local:
					_offsets.Remove(local.Symbol);
					break;
				
				case ExpressionInstruction
				{
					Value: AssignValue
					{
						Left: VariableValue { Variable.Symbol: LocalVariableSymbol target }
					} assignment
				}:
					if (GetOffset(assignment.Right) is { } assigned)
						_offsets[target] = assigned;
					else
						_offsets.Remove(target);
					
					break;
			}
		}
		
		private void Classify(List<MemoryEvent> events)
		{
			foreach (var memoryEvent in events)
			{
				order++;
				switch (memoryEvent)
				{
					case DefineEvent e:
						RejectWhole(new Place(e.Local, []));
						break;
					
					case StorageDeadEvent e:
						RejectWhole(new Place(e.Local, []));
						break;
					
					case WriteEvent e:
						Record(e.Place, new LeafShape(PathState.Initialized, []), false, e.Location);
						break;
					
					case AccessEvent { Kind: AccessKind.Move } e:
						Record(e.Place, null, true, e.Location, true);
						break;
					
					case AccessEvent { Kind: AccessKind.Address } e:
						RejectWhole(e.Place.AddressedStorage);
						break;
					
					case AccessEvent e:
						Record(e.Place, null, true, e.Location);
						break;
					
					case DropEvent e:
						Record(e.Place, null, true, e.Location);
						break;
				}
			}
		}
		
		private void RejectWhole(Place place)
		{
			if (skipper._paths.FindPrefix(place) is ({ } path, var depth) && depth == place.Path.Length &&
			    ContainsElementArray(path))
				rejected = true;
		}
		
		private bool ContainsElementArray(MovePath path) =>
			skipper._paths.ElementArrays.Any(array => array.Index >= path.Index && array.Index < path.End);
		
		private void Record(Place place, Shape? effect, bool observes, SourceLocation location, bool moves = false)
		{
			if (skipper._paths.FindPrefix(place) is not ({ } path, var depth))
				return;
			
			if (!skipper._paths.TracksElements(path))
			{
				if (depth == place.Path.Length && ContainsElementArray(path))
					rejected = true;
				
				return;
			}
			
			var position = depth;
			BigInteger? offset = null;
			for (; position < place.Path.Length; position++)
			{
				if (place.Path[position] is FieldProjection)
					continue;
				
				if (place.Path[position] is not IndexProjection { Source: { } source } index)
				{
					rejected = true;
					return;
				}
				
				if (GetOffset(source) is { } found)
				{
					offset = found;
					break;
				}
				
				if (index.Index is null || Mentions(source))
				{
					rejected = true;
					return;
				}
			}
			
			if (offset is null || (effect is not null || moves) && position != place.Path.Length - 1)
			{
				rejected = true;
				return;
			}
			
			var prefix = place.Path[depth..position].ToArray();
			var group = Groups.Find(existing =>
				existing.Array.Index == path.Index && existing.Prefix.SequenceEqual(prefix));
			
			if (group is null)
				Groups.Add(group = new Group(path, prefix));
			
			if (moves)
				effect = new LeafShape(PathState.Moved, [new MoveSite(path, location, prefix.Length + 1)]);
			
			group.Touches.Add(new Touch(order, offset.Value, effect, observes));
		}
		
		private BigInteger? GetOffset(Value value) => value switch
		{
			VariableValue { Variable.Symbol: LocalVariableSymbol local } when _offsets.TryGetValue(local,
					out var offset) =>
				offset,
			BinOpValue { Op: BinaryOperation.Addition or BinaryOperation.WrappingAddition } v =>
				GetOffset(v.Left) is { } left && GetConstant(v.Right) is { } right ? left + right
				: GetConstant(v.Left) is { } first && GetOffset(v.Right) is { } second ? first + second
				: null,
			BinOpValue { Op: BinaryOperation.Subtraction or BinaryOperation.WrappingSubtraction } v =>
				GetOffset(v.Left) is { } left && GetConstant(v.Right) is { } right ? left - right : null,
			ConversionValue { Conversion: IntegerConversion or IdentityConversion } v => GetOffset(v.Source),
			_ => null
		};
		
		private BigInteger? GetConstant(Value value) =>
			Mentions(value) ? null : skipper._values.EvaluateInteger(value, known);
		
		private bool Mentions(Value value) => _varying.Any(symbol => Mentions(value, symbol));
		
		private static bool Mentions(Value value, LocalVariableSymbol symbol) => value switch
		{
			VariableValue v => v.Variable.Symbol == symbol,
			BinOpValue v => Mentions(v.Left, symbol) || Mentions(v.Right, symbol),
			UnaryOpValue v => Mentions(v.Operand, symbol),
			ConversionValue v => Mentions(v.Source, symbol),
			ConstantValue => false,
			_ => true
		};
	}
}