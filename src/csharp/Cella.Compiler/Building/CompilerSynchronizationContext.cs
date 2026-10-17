using System.Collections.Concurrent;

namespace Cella.Compiler.Building;

internal sealed class CompilerSynchronizationContext : SynchronizationContext
{
	private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
	
	public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
	
	public void Complete() => _queue.CompleteAdding();
	
	public void Run()
	{
		foreach (var (callback, state) in _queue.GetConsumingEnumerable())
			callback(state);
	}
}