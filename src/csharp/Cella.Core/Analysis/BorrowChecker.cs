using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Analysis;

public sealed class BorrowChecker(TypePool typePool, DiagnosticList diagnostics)
{
	private enum Ending
	{
		Moved,
		Reassigned,
		Dropped
	}
	
	private readonly record struct Invalidation(Ending Ending, SourceLocation Location);
	
	private readonly HashSet<SourceLocation> _reported = [];
	
	public void Check(LoweredFunction function, Dictionary<BasicBlock, List<MemoryEvent>> events)
	{
		foreach (var (block, entryState) in Solve(function, events))
		{
			var state = entryState.Copy();
			foreach (var memoryEvent in events[block])
			{
				CheckUse(memoryEvent, state);
				Transfer(state, memoryEvent);
			}
		}
	}
	
	private Dictionary<BasicBlock, BorrowState> Solve(LoweredFunction function,
		Dictionary<BasicBlock, List<MemoryEvent>> events)
	{
		var order = CfgUtils.GetReversePostorder(function);
		var entryStates = new Dictionary<BasicBlock, BorrowState>();
		var exitStates = new Dictionary<BasicBlock, BorrowState>();
		var changed = true;
		while (changed)
		{
			changed = false;
			foreach (var block in order)
			{
				var state = new BorrowState();
				foreach (var predecessor in block.GetPredecessors())
				{
					if (exitStates.TryGetValue(predecessor, out var exitState))
						state.JoinWith(exitState);
				}
				
				entryStates[block] = state.Copy();
				foreach (var memoryEvent in events[block])
					Transfer(state, memoryEvent);
				
				if (!exitStates.TryGetValue(block, out var previous))
				{
					exitStates[block] = state;
					changed = true;
				}
				else if (previous.JoinWith(state))
					changed = true;
			}
		}
		
		return entryStates;
	}
	
	private void CheckUse(MemoryEvent memoryEvent, BorrowState state)
	{
		switch (memoryEvent)
		{
			case AccessEvent e when e.Place.Root is LocalVariableSymbol { IsBorrowBinding: true } ||
			                        typePool.HoldsBorrows(e.Type):
				ReportEnded(e.Place.Root, e.Location, state, null);
				break;
			
			case DropEvent { Instruction.Value.Type: var type } e
				when typePool.NeedsDrop(type) && typePool.HoldsBorrows(type):
				var isNamed = e.Place.Path.IsEmpty && !e.Instruction.IsReassignment &&
				              !e.Place.Root.Name.StartsWith('.');
				
				ReportEnded(e.Place.Root, e.Location, state, isNamed ? $"'{e.Place.Root.Name}' is dropped here" : null);
				break;
		}
	}
	
	private void ReportEnded(VariableSymbol holder, SourceLocation location, BorrowState state, string? context)
	{
		var endings = state.Get(holder).Values
			.SelectMany(static invalidations => invalidations)
			.Distinct()
			.OrderBy(static invalidation => invalidation.Location.Range.Start)
			.ToList();
		
		if (endings.Count == 0 || !_reported.Add(location))
			return;
		
		var message = endings[0].Ending switch
		{
			Ending.Moved => "Cannot use borrows of moved values",
			Ending.Reassigned => "Cannot use borrows of reassigned values",
			_ => "Cannot use borrows of dropped values"
		};
		
		var hints = endings
			.Select(ending => ending.Location == location
				? $"{Describe(ending.Ending)} here in an earlier iteration"
				: $"{Describe(ending.Ending)} on line {ending.Location.GetLineColumn().Line}")
			.Distinct();
		
		diagnostics.Add(new(DiagnosticSeverity.Error, location, message)
		{
			Hints = context is null ? [..hints] : [context, ..hints]
		});
	}
	
	private static string Describe(Ending ending) => ending switch
	{
		Ending.Moved => "Moved",
		Ending.Reassigned => "Reassigned",
		_ => "Dropped"
	};
	
	private void Transfer(BorrowState state, MemoryEvent memoryEvent)
	{
		switch (memoryEvent)
		{
			case DefineEvent e:
				state.Set(e.Local, Sources(e.Value, state));
				break;
			
			case WriteEvent { Place: { Root: LocalVariableSymbol { IsBorrowBinding: true } binding } } e:
				WriteThrough([..state.Get(binding).Keys], Sources(e.Value, state), state);
				break;
			
			case WriteEvent e:
				var written = e.Value is CallValue { Function.Symbol.Kind: FunctionKind.Constructor } constructor
					? CallSources(constructor.Function.Signature, [..constructor.Arguments.Skip(1)], 1, state)
					: Sources(e.Value, state);
				
				if (e.Place.Path.IsEmpty)
					state.Set(e.Place.Root, written);
				else
					state.Add(e.Place.Root, written);
				
				break;
			
			case IndirectWriteEvent e:
				WriteThrough(BorrowSources(e.Target, state), Sources(e.Value, state), state);
				break;
			
			case AccessEvent { Kind: AccessKind.Move } e:
				state.End(e.Place, new(Ending.Moved, e.Location));
				break;
			
			case DropEvent e:
				state.End(e.Place, new(e.Instruction.IsReassignment ? Ending.Reassigned : Ending.Dropped, e.Location));
				break;
			
			case StorageDeadEvent e:
				state.End(new(e.Local, []), new(Ending.Dropped, e.Location));
				state.Remove(e.Local);
				break;
		}
	}
	
	private static void WriteThrough(List<Place> targets, List<Place> written, BorrowState state)
	{
		foreach (var root in targets.Select(static t => t.Root).Distinct())
			state.Add(root, written);
	}
	
	private List<Place> Sources(Value value, BorrowState state) => value switch
	{
		UnaryOpValue { Op: UnaryOperation.AddressOf } v => BorrowSources(v.Operand, state),
		MoveValue v => ReadSources(v.Place, state),
		VariableValue or AccessValue { Member: FieldSymbol } or IndexerValue or EnumPayloadValue
			or UnaryOpValue { Op: UnaryOperation.Dereference } => ReadSources(value, state),
		ConversionValue v => Sources(v.Source, state),
		AssignValue v => ReadSources(v.Left, state),
		CallValue v when typePool.HoldsBorrows(v.Type) => CallSources(v.Function.Signature, v.Arguments, 0, state),
		IndirectCallValue v when typePool.HoldsBorrows(v.Type) => IndirectCallSources(v, state),
		EnumValue v => [..v.Payload.SelectMany(payload => Sources(payload, state))],
		ArrayValue v => [..v.Elements.SelectMany(element => Sources(element, state))],
		_ => []
	};
	
	private List<Place> ReadSources(Value place, BorrowState state)
	{
		if (EventLinearizer.GetPlace(place) is { } tracked)
			return tracked.Root is LocalVariableSymbol { IsBorrowBinding: true } binding
				? Expand([..state.Get(binding).Keys], state)
				: [..state.Get(tracked.Root).Keys];
		
		return GetBase(place) switch
		{
			UnaryOpValue { Op: UnaryOperation.Dereference } deref => Expand(Sources(deref.Operand, state), state),
			var root when root != place => Sources(root, state),
			_ => []
		};
	}
	
	private List<Place> BorrowSources(Value place, BorrowState state)
	{
		if (EventLinearizer.GetPlace(place) is { } tracked)
			return tracked.Root is LocalVariableSymbol { IsBorrowBinding: true } binding
				? [..state.Get(binding).Keys]
				: [tracked];
		
		return GetBase(place) is UnaryOpValue { Op: UnaryOperation.Dereference } deref
			? Sources(deref.Operand, state)
			: [];
	}
	
	private static List<Place> Expand(List<Place> sources, BorrowState state) =>
		[..sources, ..sources.SelectMany(s => state.Get(s.Root).Keys)];
	
	private static Value GetBase(Value place) => place switch
	{
		AccessValue { Member: FieldSymbol } v => GetBase(v.Target),
		IndexerValue v => GetBase(v.Target),
		EnumPayloadValue v => GetBase(v.Target),
		_ => place
	};
	
	private List<Place> CallSources(FunctionSignature signature, IReadOnlyList<Value> arguments, int first,
		BorrowState state)
	{
		var sources = new List<Place>();
		for (var i = 0; i < arguments.Count && first + i < signature.ParameterTypes.Length; i++)
			sources.AddRange(ArgumentSources(arguments[i], signature.GetMode(first + i),
				signature.GetDeclaredType(first + i), state));
		
		return sources;
	}
	
	private List<Place> IndirectCallSources(IndirectCallValue call, BorrowState state)
	{
		var type = call.FunctionType;
		var sources = new List<Place>();
		for (var i = 0; i < call.Arguments.Length && i < type.ParameterTypes.Length; i++)
			sources.AddRange(ArgumentSources(call.Arguments[i], type.ParameterModes[i], type.GetDeclaredType(i),
				state));
		
		return sources;
	}
	
	private List<Place> ArgumentSources(Value argument, ParameterMode mode, TypeSymbol declared, BorrowState state) =>
		mode == ParameterMode.Mut || typePool.PassesByPointer(declared, mode)
			? Expand(Sources(argument, state), state)
			: Sources(argument, state);
	
	private static bool Overlaps(Place first, Place second) => first.Root == second.Root &&
	                                                           !first.Path.Zip(second.Path).Any(static pair =>
		                                                           AreDisjoint(pair.First, pair.Second));
	
	private static bool AreDisjoint(Projection first, Projection second) => (first, second) switch
	{
		(FieldProjection a, FieldProjection b) => a.Field != b.Field,
		(IndexProjection { Index: { } a }, IndexProjection { Index: { } b }) => a != b,
		(PayloadProjection a, PayloadProjection b) => a.Case == b.Case && a.Index != b.Index,
		_ => false
	};
	
	private sealed class PlaceComparer : IEqualityComparer<Place>
	{
		public static PlaceComparer Instance { get; } = new();
		
		public bool Equals(Place? first, Place? second) => first is not null && second is not null &&
		                                                   first.Root == second.Root &&
		                                                   first.Path.SequenceEqual(second.Path);
		
		public int GetHashCode(Place place) => HashCode.Combine(place.Root, place.Path.Length);
	}
	
	private sealed class BorrowState
	{
		private static readonly Dictionary<Place, ImmutableHashSet<Invalidation>> _none = new(PlaceComparer.Instance);
		
		private readonly Dictionary<VariableSymbol, Dictionary<Place, ImmutableHashSet<Invalidation>>> _holds = [];
		
		public BorrowState Copy()
		{
			var copy = new BorrowState();
			foreach (var (holder, sources) in _holds)
				copy._holds[holder] = new(sources, PlaceComparer.Instance);
			
			return copy;
		}
		
		public IReadOnlyDictionary<Place, ImmutableHashSet<Invalidation>> Get(VariableSymbol holder) =>
			_holds.GetValueOrDefault(holder, _none);
		
		public void Set(VariableSymbol holder, List<Place> sources)
		{
			if (sources.Count == 0)
			{
				_holds.Remove(holder);
				return;
			}
			
			var fresh = new Dictionary<Place, ImmutableHashSet<Invalidation>>(PlaceComparer.Instance);
			foreach (var source in sources)
				fresh[source] = [];
			
			_holds[holder] = fresh;
		}
		
		public void Add(VariableSymbol holder, List<Place> sources)
		{
			if (sources.Count == 0)
				return;
			
			if (!_holds.TryGetValue(holder, out var own))
			{
				own = new(PlaceComparer.Instance);
				_holds[holder] = own;
			}
			
			foreach (var source in sources)
				own.TryAdd(source, []);
		}
		
		public void Remove(VariableSymbol holder) => _holds.Remove(holder);
		
		public void End(Place place, Invalidation invalidation)
		{
			foreach (var sources in _holds.Values)
			{
				foreach (var source in sources.Keys.Where(s => Overlaps(s, place)).ToList())
					sources[source] = sources[source].Add(invalidation);
			}
		}
		
		public bool JoinWith(BorrowState other)
		{
			var changed = false;
			foreach (var (holder, sources) in other._holds)
			{
				if (!_holds.TryGetValue(holder, out var own))
				{
					_holds[holder] = new(sources, PlaceComparer.Instance);
					changed = true;
					continue;
				}
				
				foreach (var (source, invalidations) in sources)
				{
					if (!own.TryGetValue(source, out var existing))
					{
						own[source] = invalidations;
						changed = true;
					}
					else if (!existing.IsSupersetOf(invalidations))
					{
						own[source] = existing.Union(invalidations);
						changed = true;
					}
				}
			}
			
			return changed;
		}
	}
}