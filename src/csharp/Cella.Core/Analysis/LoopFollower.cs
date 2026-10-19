using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Lowering;
using Cella.Core.Symbols;

namespace Cella.Core.Analysis;

public readonly record struct FollowedVisit(BasicBlock Block, KnownValues Known, InitState State);

public sealed class LoopFollower
{
	private readonly EventLinearizer _linearizer;
	private readonly ValueEvaluator _values;
	private readonly HashSet<LocalVariableSymbol> _tracked;
	private readonly Action<InitState, MemoryEvent> _transfer;
	private readonly Dictionary<BasicBlock, int> _order = [];
	private readonly Dictionary<BasicBlock, Loop> _followedHeads = [];
	private readonly Dictionary<(Context? Parent, Loop Loop, KnownValues Known), Context> _contexts = [];
	private readonly Dictionary<(BasicBlock Block, Context Context), Node> _nodes = [];
	private readonly PriorityQueue<(BasicBlock Block, Context Context), (int Sequence, int Order)> _queue = new();
	private readonly HashSet<(BasicBlock Block, Context Context)> _queued = [];
	private readonly PassSkipper _skipper;
	
	private KnownValues known = KnownValues.Empty;
	
	public LoopFollower(LoweredFunction function, MovePaths paths, ShapeLayout layout, ValueEvaluator values,
		HashSet<LocalVariableSymbol> tracked, Action<InitState, MemoryEvent> transfer)
	{
		_values = values;
		_tracked = tracked;
		_transfer = transfer;
		_linearizer = new EventLinearizer(function)
		{
			ResolveIndex = (target, index) => paths.FindPrefix(target) is ({ } array, var depth) &&
			                                  paths.TracksElements(array) &&
			                                  layout.GetPartType(array.Type, target.Path.AsSpan()[depth..]) is
				                                  { } type &&
			                                  values.EvaluateInteger(index, known) is { } resolved &&
			                                  layout.IsInBounds(type, resolved)
				? resolved
				: null
		};
		
		_skipper = new PassSkipper(this, paths, layout, values);
	}
	
	public Loop? Unfollowable { get; private set; }
	
	public List<FollowedVisit>? Solve(IReadOnlyList<BasicBlock> order, IEnumerable<Loop> followed, InitState entryState)
	{
		for (var i = 0; i < order.Count; i++)
			_order[order[i]] = i;
		
		foreach (var loop in followed)
			_followedHeads[loop.Head] = loop;
		
		if (order.Count == 0)
			return [];
		
		var root = new Context(null, null, 0);
		Merge(order[0], Enter(root, order[0], KnownValues.Empty), KnownValues.Empty, entryState);
		while (_queue.TryDequeue(out var key, out _))
		{
			_queued.Remove(key);
			if (!Step(key.Block, key.Context))
				return null;
		}
		
		return _nodes.Select(static node => new FollowedVisit(node.Key.Block, node.Value.Known, node.Value.State))
			.ToList();
	}
	
	public List<MemoryEvent> Walk(BasicBlock block, ref KnownValues values)
	{
		var events = new List<MemoryEvent>();
		foreach (var instruction in block.Instructions)
		{
			events.AddRange(Linearize(instruction, values));
			values = Advance(instruction, values);
		}
		
		events.AddRange(Linearize(block.Terminator, values));
		return events;
	}
	
	internal List<MemoryEvent> Linearize(IInstruction instruction, KnownValues values)
	{
		known = values;
		return _linearizer.Linearize(instruction);
	}
	
	internal List<MemoryEvent> Linearize(IBlockTerminator terminator, KnownValues values)
	{
		known = values;
		return _linearizer.Linearize(terminator);
	}
	
	internal KnownValues Advance(IInstruction instruction, KnownValues values)
	{
		values = Update(instruction, values);
		return instruction is EndScopeInstruction
			? Linearize(instruction, values).OfType<StorageDeadEvent>()
				.Aggregate(values, static (current, dead) => current.With(dead.Local, null))
			: values;
	}
	
	private bool Step(BasicBlock block, Context context)
	{
		var node = _nodes[(block, context)];
		var values = node.Known;
		var state = node.State.Copy();
		foreach (var memoryEvent in Walk(block, ref values))
			_transfer(state, memoryEvent);
		
		foreach (var successor in GetSuccessors(block, context, values))
		{
			if (FindTarget(context, successor, values, state) is not var (target, targetValues, targetState))
				return false;
			
			Merge(successor, target, targetValues, targetState);
		}
		
		return true;
	}
	
	private IEnumerable<BasicBlock> GetSuccessors(BasicBlock block, Context context, KnownValues values)
	{
		if (block.Terminator is not ConditionalBranchTerminator { IsErasedCall: false } branch)
			return CfgUtils.GetSuccessors(block);
		
		var decided = _values.Evaluate(branch.Condition, values) as BoolConstant;
		var successors = decided is null
			? CfgUtils.GetSuccessors(block)
			: [decided.Value ? branch.TrueTarget : branch.FalseTarget];
		
		if (decided is not null && CfgUtils.GetSuccessors(block).Length > 1)
		{
			for (var current = context; current.Loop is { } loop; current = current.Parent!)
			{
				if (loop.Body.Contains(block) &&
				    (!loop.Body.Contains(branch.TrueTarget) || !loop.Body.Contains(branch.FalseTarget)))
					current.IsDecided = true;
			}
		}
		
		return successors;
	}
	
	private (Context Context, KnownValues Values, InitState State)? FindTarget(Context context, BasicBlock successor,
		KnownValues values, InitState state)
	{
		var target = context;
		while (target.Loop is { } loop && (!loop.Body.Contains(successor) || loop.Head == successor))
		{
			if (loop.Head == successor)
			{
				if (!target.IsDecided)
				{
					Unfollowable = loop;
					return null;
				}
				
				var head = _nodes[(loop.Head, target)];
				if (_skipper.Skip(loop, head.Known, head.State, values, state) is var (skippedValues, skippedState))
					(values, state) = (skippedValues, skippedState);
			}
			
			target = target.Parent!;
		}
		
		return (Enter(target, successor, values), values, state);
	}
	
	private Context Enter(Context parent, BasicBlock block, KnownValues values)
	{
		if (!_followedHeads.TryGetValue(block, out var loop))
			return parent;
		
		var key = (parent, loop, values);
		if (!_contexts.TryGetValue(key, out var context))
			_contexts[key] = context = new Context(parent, loop, _contexts.Count + 1);
		
		return context;
	}
	
	private void Merge(BasicBlock block, Context context, KnownValues values, InitState state)
	{
		var key = (block, context);
		bool changed;
		if (!_nodes.TryGetValue(key, out var node))
		{
			_nodes[key] = new Node(values, state.Copy());
			changed = true;
		}
		else
		{
			var intersected = node.Known.Intersect(values);
			changed = !intersected.Equals(node.Known);
			node.Known = intersected;
			changed |= node.State.JoinWith(state);
		}
		
		if (changed && _queued.Add(key))
			_queue.Enqueue(key, (context.Sequence, _order[block]));
	}
	
	private KnownValues Update(IInstruction instruction, KnownValues values) => instruction switch
	{
		LocalVarInstruction i when _tracked.Contains(i.Symbol) => values.With(i.Symbol, i.Initializer switch
		{
			UndefValue => null,
			DefaultValue => Zero(i.Symbol.Type),
			var initializer => _values.Evaluate(initializer, values)
		}),
		ExpressionInstruction
			{
				Value: AssignValue { Left: VariableValue { Variable.Symbol: LocalVariableSymbol local } } v
			}
			when _tracked.Contains(local) => values.With(local, _values.Evaluate(v.Right, values)),
		_ => values
	};
	
	private static Constant? Zero(TypeSymbol type) => type switch
	{
		IntegerStorageType => new IntegerConstant(type, BigInteger.Zero),
		_ when type == NativeSymbols.Bool => BoolConstant.False,
		_ => null
	};
	
	private sealed class Context(Context? parent, Loop? loop, int sequence)
	{
		public Context? Parent { get; } = parent;
		public Loop? Loop { get; } = loop;
		public int Sequence { get; } = sequence;
		public bool IsDecided { get; set; }
	}
	
	private sealed class Node(KnownValues known, InitState state)
	{
		public KnownValues Known { get; set; } = known;
		public InitState State { get; } = state;
	}
}