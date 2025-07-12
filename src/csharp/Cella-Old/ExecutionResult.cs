namespace Cella.Diagnostics;

public readonly struct ExecutionResult
{
	public bool IsSuccess { get; }
	public TimeSpan ElapsedTime { get; }
	
	public ExecutionResult(bool isSuccess, TimeSpan elapsedTime)
	{
		IsSuccess = isSuccess;
		ElapsedTime = elapsedTime;
	}
}