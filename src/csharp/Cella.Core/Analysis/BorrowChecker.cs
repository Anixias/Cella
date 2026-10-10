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
	
	private enum Escape
	{
		Closure,
		Local,
		Unstored,
		Owned,
		Copied,
		Borrowed,
		ViewRecord
	}
	
	private readonly record struct Invalidation(Ending Ending, SourceLocation Location);
	
	private readonly HashSet<SourceLocation> _reported = [];
	private readonly ClosureEnvironment _environment = new();
	private readonly Dictionary<LocalVariableSymbol, BindingTarget> _targets = [];
	private readonly Dictionary<LocalVariableSymbol, BindingTarget> _formerTargets = [];
	private HashSet<VariableSymbol> returned = [];
	private Dictionary<ParameterSymbol, Escape?> parameterEscapes = [];
	private ParameterSymbol? receiver;
	private ParameterSymbol? constructorSelf;
	
	public void Check(LoweredFunction function, Dictionary<BasicBlock, List<MemoryEvent>> events)
	{
		var symbol = function.Info.Symbol;
		receiver = symbol.Kind is FunctionKind.Constructor or FunctionKind.Destructor or FunctionKind.Method
			? symbol.Parameters[0]
			: null;
		
		constructorSelf = symbol.Kind == FunctionKind.Constructor ? receiver : null;
		returned = GetReturned(function, events);
		parameterEscapes = GetParameterEscapes(function.Info);
		foreach (var (block, entryState) in Solve(function, events))
		{
			var state = entryState.Copy();
			foreach (var memoryEvent in events[block])
			{
				CheckUse(memoryEvent, state);
				CheckReturn(memoryEvent, state);
				CheckStore(memoryEvent, state);
				Transfer(state, memoryEvent);
			}
		}
	}
	
	private Dictionary<BasicBlock, BorrowState> Solve(LoweredFunction function,
		Dictionary<BasicBlock, List<MemoryEvent>> events)
	{
		var order = CfgUtils.GetReversePostorder(function);
		var initialState = CreateInitialState(function);
		var entryStates = new Dictionary<BasicBlock, BorrowState>();
		var exitStates = new Dictionary<BasicBlock, BorrowState>();
		var changed = true;
		while (changed)
		{
			changed = false;
			foreach (var block in order)
			{
				var state = block == function.Blocks[0] ? initialState.Copy() : new BorrowState();
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
	
	private BorrowState CreateInitialState(LoweredFunction function)
	{
		var state = new BorrowState();
		var signature = function.Info.Signature;
		var parameters = function.Info.Symbol.Parameters;
		for (var i = 0; i < parameters.Length; i++)
		{
			if (parameters[i] != constructorSelf && typePool.HoldsBorrows(signature.GetDeclaredType(i)))
				state.Set(parameters[i], [new Place(new HeldBorrows(parameters[i]), [])]);
		}
		
		return state;
	}
	
	private static HashSet<VariableSymbol> GetReturned(LoweredFunction function,
		Dictionary<BasicBlock, List<MemoryEvent>> events)
	{
		var roots = new HashSet<VariableSymbol>();
		foreach (var block in function.Blocks)
		{
			if (block.Terminator is ReturnTerminator { Value: { } value } &&
			    EventLinearizer.GetPlace(value is MoveValue move ? move.Place : value) is { } place)
				roots.Add(place.Root);
		}
		
		var memoryEvents = events.Values.SelectMany(static list => list).ToList();
		var merged = memoryEvents
			.OfType<DefineEvent>()
			.Where(static e => e.Kind == DefineKind.Undef && e.Local.Name.StartsWith('.'))
			.Select(static e => e.Local)
			.ToHashSet();
		
		var changed = true;
		while (changed)
		{
			changed = false;
			foreach (var memoryEvent in memoryEvents)
			{
				if (GetCopy(memoryEvent) is { } copy && merged.Contains(copy.Source) && roots.Contains(copy.Target) &&
				    roots.Add(copy.Source))
					changed = true;
			}
		}
		
		return roots;
	}
	
	private static (VariableSymbol Target, VariableSymbol Source)? GetCopy(MemoryEvent memoryEvent) =>
		memoryEvent switch
		{
			DefineEvent e when GetCopied(e.Value) is { } source => (e.Local, source),
			WriteEvent { Place.Path.IsEmpty: true } e when GetCopied(e.Value) is { } source => (e.Place.Root, source),
			_ => null
		};
	
	private static VariableSymbol? GetCopied(Value value) =>
		(value is MoveValue move ? move.Place : value) is VariableValue variable ? variable.Variable.Symbol : null;
	
	private Dictionary<ParameterSymbol, Escape?> GetParameterEscapes(FunctionInfo function) =>
		function.Symbol.Parameters
			.Select((parameter, i) => (Parameter: parameter, Escape: GetEscape(function, i)))
			.ToDictionary(static pair => pair.Parameter, static pair => pair.Escape);
	
	private void CheckUse(MemoryEvent memoryEvent, BorrowState state)
	{
		switch (memoryEvent)
		{
			case AccessEvent e when !returned.Contains(e.Place.Root) &&
			                        (e.Place.Root is LocalVariableSymbol { IsBorrowBinding: true } ||
			                         typePool.HoldsBorrows(e.Type)):
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
	
	private void CheckReturn(MemoryEvent memoryEvent, BorrowState state)
	{
		switch (memoryEvent)
		{
			case DefineEvent e when returned.Contains(e.Local) && !IsReturnedCopy(e.Value):
				CheckEscape(Stored(e.Value, state), e.Value, e.Location);
				break;
			
			case WriteEvent e when returned.Contains(e.Place.Root) && !IsReturnedCopy(e.Value):
				CheckEscape(Written(e, state), e.Value, e.Location);
				break;
		}
	}
	
	private bool IsReturnedCopy(Value value) => GetCopied(value) is { } copied && returned.Contains(copied);
	
	private void CheckEscape(List<Place> written, Value value, SourceLocation fallback)
	{
		var location = GetLocation(value, fallback);
		if (FindEscape(written) is { } escape && _reported.Add(location))
			diagnostics.Add(new(DiagnosticSeverity.Error, location, escape == Escape.Closure
				? "Cannot return closures that capture variables"
				: $"Cannot return borrows of {Describe(escape)}"));
	}
	
	private static SourceLocation GetLocation(Value value, SourceLocation fallback) =>
		value.SourceLocation == SourceLocation.None ? fallback : value.SourceLocation;
	
	private void CheckStore(MemoryEvent memoryEvent, BorrowState state)
	{
		switch (memoryEvent)
		{
			case WriteEvent { Place.Root: LocalVariableSymbol { IsBorrowBinding: true } binding } e:
				CheckTargets([..state.Get(binding).Keys], Stored(e.Value, state), e.Value, e.Location, state);
				break;
			
			case WriteEvent e:
				CheckTargets([e.Place], Written(e, state), e.Value, e.Location, state);
				break;
			
			case IndirectWriteEvent e when GetBase(e.Target) is UnaryOpValue
			{
				Op: UnaryOperation.Dereference,
				Operand.Type: BorrowType
			}:
				CheckTargets(BorrowSources(e.Target, state), Stored(e.Value, state), e.Value, e.Location, state);
				break;
		}
	}
	
	private void CheckTargets(List<Place> targets, List<Place> written, Value value, SourceLocation fallback,
		BorrowState state)
	{
		var origins = written.Where(static source => source.Root is not BindingTarget).ToList();
		if (origins.Count == 0)
			return;
		
		var location = GetLocation(value, fallback);
		foreach (var root in Roots(targets))
		{
			var message = root switch
			{
				HeldBorrows => "Cannot store borrows through borrows from parameters",
				_ when origins.Any(source => source.Root == root) => "Cannot store borrows of values in themselves",
				ParameterSymbol parameter when parameter == constructorSelf => FindEscape(origins) switch
				{
					Escape.Closure => "Cannot store closures that capture variables in 'self'",
					{ } escape => $"Cannot store borrows of {Describe(escape)} in 'self'",
					null => null
				},
				ParameterSymbol { Mode: ParameterMode.Mut } parameter when !IsHeld(origins, parameter, state) =>
					parameter == receiver
						? "Cannot store new borrows in 'self'"
						: "Cannot store new borrows in 'mut' parameters",
				_ => null
			};
			
			if (message is not null && _reported.Add(location))
				diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
		}
	}
	
	private static IEnumerable<VariableSymbol> Roots(List<Place> targets) => targets
		.Select(static target => target.Root)
		.Where(static root => root is not BindingTarget)
		.Distinct();
	
	private static bool IsHeld(List<Place> written, ParameterSymbol parameter, BorrowState state) =>
		written.All(source => source.Root is HeldBorrows { Parameter: var owner } && owner == parameter ||
		                      state.Get(parameter).ContainsKey(source));
	
	private Escape? FindEscape(IEnumerable<Place> sources) => sources.Select(source => FindEscape(source.Root)).Min();
	
	private Escape? FindEscape(VariableSymbol root) => root switch
	{
		ClosureEnvironment => Escape.Closure,
		ParameterSymbol parameter => parameterEscapes[parameter],
		LocalVariableSymbol local => local.Name.StartsWith('.') ? Escape.Unstored : Escape.Local,
		_ => null
	};
	
	private static string Describe(Escape escape) => escape switch
	{
		Escape.Local => "local variables",
		Escape.Unstored => "unstored values",
		Escape.Owned => "'own' parameters",
		Escape.Copied => "copied parameters",
		Escape.Borrowed => "parameters other than 'self'",
		_ => "view records"
	};
	
	private void Transfer(BorrowState state, MemoryEvent memoryEvent)
	{
		switch (memoryEvent)
		{
			case DefineEvent e:
				ForgetTarget(e.Local, state);
				state.Set(e.Local, Sources(e.Value, state));
				break;
			
			case WriteEvent { Place: { Root: LocalVariableSymbol { IsBorrowBinding: true } binding } } e:
				WriteThrough([..state.Get(binding).Keys], Stored(e.Value, state), state);
				break;
			
			case WriteEvent e:
				state.Revive(e.Place);
				if (e.Place.Path.IsEmpty)
					state.Set(e.Place.Root, Written(e, state));
				else
					state.Add(e.Place.Root, Written(e, state));
				
				break;
			
			case IndirectWriteEvent e:
				WriteThrough(BorrowSources(e.Target, state), Stored(e.Value, state), state);
				break;
			
			case AccessEvent { Kind: AccessKind.Move } e:
				state.End(e.Place, new(Ending.Moved, e.Location));
				break;
			
			case DropEvent e:
				End(e.Place, new(e.Instruction.IsReassignment ? Ending.Reassigned : Ending.Dropped, e.Location), state);
				break;
			
			case StorageDeadEvent e:
				ForgetTarget(e.Local, state);
				state.End(new(e.Local, []), new(Ending.Dropped, e.Location));
				state.Remove(e.Local);
				break;
		}
	}
	
	private void End(Place place, Invalidation invalidation, BorrowState state)
	{
		state.End(place, invalidation);
		if (FindTarget(place) is { } target)
			state.End(target, invalidation);
	}
	
	private Place? FindTarget(Place place) => place is
	{
		Root: LocalVariableSymbol { IsBorrowBinding: true } binding,
		Path: [DerefProjection, .. var path]
	}
		? new(_targets.GetOrAdd(binding), path)
		: null;
	
	private void ForgetTarget(LocalVariableSymbol local, BorrowState state)
	{
		if (_targets.TryGetValue(local, out var target))
			state.Retarget(target, _formerTargets.GetOrAdd(local));
	}
	
	private static void WriteThrough(List<Place> targets, List<Place> written, BorrowState state)
	{
		foreach (var root in Roots(targets))
			state.Add(root, written);
	}
	
	private List<Place> Written(WriteEvent write, BorrowState state) => write.Value switch
	{
		CallValue
		{
			Function: { Symbol.Kind: FunctionKind.Constructor, DeclaredSignature: var signature }
		} constructor => typePool.HoldsBorrows(signature.GetDeclaredType(0))
			? Results(CallSources(constructor.Function, [..constructor.Arguments.Skip(1)], 1, state),
				signature.GetDeclaredType(0))
			: [],
		var value => Stored(value, state)
	};
	
	private List<Place> Stored(Value value, BorrowState state) =>
		typePool.HoldsBorrows(value.Type) ? Sources(value, state) : [];
	
	private List<Place> Sources(Value value, BorrowState state)
	{
		if (!CarriesSources(value.Type))
			return [];
		
		return value switch
		{
			UnaryOpValue { Op: UnaryOperation.AddressOf } v =>
				[..BorrowSources(v.Operand, state), ..TargetSources(v.Operand)],
			MoveValue v => ReadSources(v.Place, state),
			VariableValue or AccessValue { Member: FieldSymbol } or IndexerValue or EnumPayloadValue
				or UnaryOpValue { Op: UnaryOperation.Dereference } => ReadSources(value, state),
			ConversionValue v => Sources(v.Source, state),
			AssignValue v => ReadSources(v.Left, state),
			CallValue v when typePool.HoldsBorrows(v.Type) => Results(CallSources(v.Function, v.Arguments, 0, state),
				v.Type),
			IndirectCallValue v when typePool.HoldsBorrows(v.Type) => Results(IndirectCallSources(v, state), v.Type),
			EnumValue v => [..v.Payload.SelectMany(payload => Sources(payload, state))],
			ArrayValue v => [..v.Elements.SelectMany(element => Sources(element, state))],
			FStrValue v => TemplateSources(v, state),
			FStrPartValue v => Sources(v.Target, state),
			ClosureValue v => [new Place(_environment, []), ..v.Captures.SelectMany(c => CaptureSources(c, state))],
			OwnClosureValue v => [..v.Captures.SelectMany(capture => Sources(capture, state))],
			_ => []
		};
	}
	
	private List<Place> Results(List<Place> sources, TypeSymbol type) => typePool.CanHoldClosures(type)
		? sources
		: [..sources.Where(static source => source.Root is not ClosureEnvironment)];
	
	private List<Place> CaptureSources(Value capture, BorrowState state)
	{
		if (capture is not UnaryOpValue { Op: UnaryOperation.AddressOf } address)
			return [];
		
		var place = EventLinearizer.GetPlace(address.Operand);
		return place is { Root: not LocalVariableSymbol { IsBorrowBinding: true } }
			? [place.Project(new CaptureProjection())]
			: BorrowSources(address.Operand, state);
	}
	
	private List<Place> TemplateSources(FStrValue template, BorrowState state)
	{
		var sources = new List<Place>();
		if (template.Text is { } text)
			sources.AddRange(Sources(text, state));
		
		if (template.Values is { } values)
			sources.AddRange(Sources(values, state));
		
		return Expand(sources, state);
	}
	
	private bool CarriesSources(TypeSymbol type) => type is PointerType || typePool.HoldsBorrows(type);
	
	private List<Place> ReadSources(Value place, BorrowState state)
	{
		if (EventLinearizer.GetPlace(place) is { } tracked)
			return tracked.Root switch
			{
				LocalVariableSymbol { IsBorrowBinding: true } binding when tracked.Path.IsEmpty =>
					Expand([..state.Get(binding).Keys], state),
				LocalVariableSymbol { IsBorrowBinding: true } binding => Held([..state.Get(binding).Keys], state),
				var root => [..state.Get(root).Keys]
			};
		
		return GetBase(place) switch
		{
			UnaryOpValue { Op: UnaryOperation.Dereference } deref => Expand(Sources(deref.Operand, state), state),
			var root when root != place => Sources(root, state),
			_ => []
		};
	}
	
	private List<Place> TargetSources(Value place) =>
		EventLinearizer.GetPlace(place) is { } tracked && FindTarget(tracked) is { } target ? [target] : [];
	
	private List<Place> BorrowSources(Value place, BorrowState state)
	{
		if (EventLinearizer.GetPlace(place) is { } tracked)
			return tracked.Root is LocalVariableSymbol { IsBorrowBinding: true } binding
				? [..state.Get(binding).Keys]
				: [tracked];
		
		return GetBase(place) switch
		{
			UnaryOpValue { Op: UnaryOperation.Dereference } deref => Sources(deref.Operand, state),
			IndexerValue element => Sources(element.Target, state),
			_ => []
		};
	}
	
	private static List<Place> Expand(List<Place> sources, BorrowState state) => [..sources, ..Held(sources, state)];
	
	private static List<Place> Held(List<Place> sources, BorrowState state) =>
		[..sources.SelectMany(s => state.Get(s.Root).Keys)];
	
	private static Value GetBase(Value place) => place switch
	{
		AccessValue { Member: FieldSymbol } v => GetBase(v.Target),
		IndexerValue { Target.Type: not StringType } v => GetBase(v.Target),
		EnumPayloadValue v => GetBase(v.Target),
		_ => place
	};
	
	private List<Place> CallSources(FunctionInfo function, IReadOnlyList<Value> arguments, int first,
		BorrowState state)
	{
		var sources = new List<Place>();
		for (var i = 0; i < arguments.Count && first + i < function.DeclaredSignature.ParameterTypes.Length; i++)
			sources.AddRange(ArgumentSources(arguments[i], GetEscape(function, first + i), state));
		
		return sources;
	}
	
	private List<Place> IndirectCallSources(IndirectCallValue call, BorrowState state)
	{
		var type = call.FunctionType;
		var sources = new List<Place>();
		for (var i = 0; i < call.Arguments.Length && i < type.ParameterTypes.Length; i++)
			sources.AddRange(ArgumentSources(call.Arguments[i],
				GetEscape(type.ParameterModes[i], type.GetDeclaredType(i), true), state));
		
		if (type.IsRef)
			sources.AddRange(Sources(call.Target, state)
				.Where(static source => source.Root is not ClosureEnvironment)
				.Select(static source => IsCapture(source) ? source with { Path = source.Path[..^1] } : source));
		
		return sources;
	}
	
	private List<Place> ArgumentSources(Value argument, Escape? escape, BorrowState state)
	{
		var sources = Sources(argument, state);
		return escape switch
		{
			null => Expand(sources, state),
			Escape.Borrowed or Escape.ViewRecord => Held(sources, state),
			_ => sources
		};
	}
	
	private Escape? GetEscape(FunctionInfo function, int index) => GetEscape(
		function.DeclaredSignature.GetMode(index), function.DeclaredSignature.GetDeclaredType(index),
		function.Symbol.Kind != FunctionKind.Method || index == 0);
	
	private Escape? GetEscape(ParameterMode mode, TypeSymbol declared, bool mayLend)
	{
		if (mode != ParameterMode.Mut && !typePool.PassesByPointer(declared, mode))
			return mode == ParameterMode.Own ? Escape.Owned : Escape.Copied;
		
		if (!mayLend)
			return Escape.Borrowed;
		
		return typePool.IsViewRecord(declared) ? Escape.ViewRecord : null;
	}
	
	private static bool IsCapture(Place source) => source.Path is [.., CaptureProjection];
	
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
	
	private sealed class HeldBorrows(ParameterSymbol parameter) : VariableSymbol(parameter.Name)
	{
		public ParameterSymbol Parameter { get; } = parameter;
	}
	
	private sealed class ClosureEnvironment() : VariableSymbol("closure");
	
	private sealed class BindingTarget() : VariableSymbol("target");
	
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
			_holds.Remove(holder);
			Add(holder, sources);
		}
		
		public void Add(VariableSymbol holder, List<Place> sources)
		{
			foreach (var source in sources.Where(source => source.Root != holder))
			{
				if (!_holds.TryGetValue(holder, out var own))
				{
					own = new(PlaceComparer.Instance);
					_holds[holder] = own;
				}
				
				own.TryAdd(source, []);
			}
		}
		
		public void Remove(VariableSymbol holder) => _holds.Remove(holder);
		
		public void Retarget(VariableSymbol from, VariableSymbol to)
		{
			foreach (var sources in _holds.Values)
			{
				foreach (var source in sources.Keys.Where(source => source.Root == from).ToList())
				{
					var invalidations = sources[source];
					var moved = source with { Root = to };
					sources.Remove(source);
					sources[moved] = sources.TryGetValue(moved, out var existing)
						? existing.Union(invalidations)
						: invalidations;
				}
			}
		}
		
		public void End(Place place, Invalidation invalidation)
		{
			var isReassignment = invalidation.Ending == Ending.Reassigned;
			foreach (var sources in _holds.Values)
			{
				var ended = sources.Keys
					.Where(source => Overlaps(source, place) && !(isReassignment && IsCapture(source)))
					.ToList();
				
				foreach (var source in ended)
					sources[source] = sources[source].Add(invalidation);
			}
		}
		
		public void Revive(Place place)
		{
			foreach (var sources in _holds.Values)
			{
				foreach (var source in sources.Keys.Where(s => IsCapture(s) && s.Root == place.Root &&
				                                               s.Path[..^1].SequenceEqual(place.Path)).ToList())
					sources[source] = [];
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