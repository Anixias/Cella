namespace Cella.Compiler.Building;

public static class CompilerThread
{
	private const int StackSize = 256 * 1024 * 1024;
	
	public static Task<T> Run<T>(Func<Task<T>> work, int stackSize = StackSize)
	{
		var result = new TaskCompletionSource<T>();
		var thread = new Thread(() =>
		{
			var context = new CompilerSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(context);
			var run = work();
			run.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
			context.Run();
			if (run.IsFaulted)
				result.SetException(run.Exception!.InnerExceptions);
			else if (run.IsCanceled)
				result.SetCanceled();
			else
				result.SetResult(run.Result);
		}, stackSize);
		
		thread.Start();
		return result.Task;
	}
}