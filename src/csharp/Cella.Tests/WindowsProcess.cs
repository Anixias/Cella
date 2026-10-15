using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cella.Tests;

[SupportedOSPlatform("windows")]
internal static partial class WindowsProcess
{
	private const uint ExtendedStartupInfoPresent = 0x00080000;
	private const int UseStandardHandles = 0x00000100;
	private const uint HandleFlagInherit = 0x00000001;
	private const nint HandleListAttribute = 0x00020002;
	
	public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executablePath,
		IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var (outputRead, outputWrite) = CreatePipe(inheritRead: false);
		var (errorRead, errorWrite) = CreatePipe(inheritRead: false);
		var (inputRead, inputWrite) = CreatePipe(inheritRead: true);
		inputWrite.Dispose();
		
		ProcessInformation information;
		try
		{
			information = Start(BuildCommandLine(executablePath, arguments),
				[inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), errorWrite.DangerousGetHandle()]);
		}
		finally
		{
			inputRead.Dispose();
			outputWrite.Dispose();
			errorWrite.Dispose();
		}
		
		new SafeWaitHandle(information.Thread, true).Dispose();
		using var process = new SafeProcessHandle(information.Process, true);
		var encoding = Console.OutputEncoding;
		var output = ReadAllAsync(outputRead, encoding);
		var error = ReadAllAsync(errorRead, encoding);
		
		using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(timeout);
		try
		{
			await WaitForExitAsync(process, timeoutSource.Token);
		}
		catch (OperationCanceledException)
		{
			_ = TerminateProcess(process, 1);
			throw new TimeoutException($"The program ran longer than {timeout.TotalSeconds} seconds");
		}
		
		if (!GetExitCodeProcess(process, out var exitCode))
			throw new Win32Exception();
		
		return ((int)exitCode, await output, await error);
	}
	
	private static unsafe (SafeFileHandle Read, SafeFileHandle Write) CreatePipe(bool inheritRead)
	{
		if (!CreatePipe(out var read, out var write, null, 0))
			throw new Win32Exception();
		
		if (!SetHandleInformation(inheritRead ? read : write, HandleFlagInherit, HandleFlagInherit))
			throw new Win32Exception();
		
		return (read, write);
	}
	
	private static unsafe ProcessInformation Start(string commandLine, nint[] inherited)
	{
		nuint size = 0;
		InitializeProcThreadAttributeList(null, 1, 0, ref size);
		var attributes = NativeMemory.Alloc(size);
		try
		{
			if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
				throw new Win32Exception();
			
			try
			{
				fixed (nint* handles = inherited)
				{
					if (!UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, handles,
						    (nuint)(sizeof(nint) * inherited.Length), null, null))
						throw new Win32Exception();
					
					var startup = new StartupInfoEx
					{
						StartupInfo = new StartupInfo
						{
							Size = sizeof(StartupInfoEx),
							Flags = UseStandardHandles,
							StandardInput = inherited[0],
							StandardOutput = inherited[1],
							StandardError = inherited[2]
						},
						AttributeList = attributes
					};
					
					var line = (commandLine + '\0').ToCharArray();
					fixed (char* text = line)
					{
						if (!CreateProcessW(null, text, null, null, true, ExtendedStartupInfoPresent, null, null,
							    &startup, out var information))
							throw new Win32Exception();
						
						return information;
					}
				}
			}
			finally
			{
				DeleteProcThreadAttributeList(attributes);
			}
		}
		finally
		{
			NativeMemory.Free(attributes);
		}
	}
	
	private static async Task<string> ReadAllAsync(SafeFileHandle pipe, Encoding encoding)
	{
		await using var stream = new FileStream(pipe, FileAccess.Read, 4096, false);
		using var reader = new StreamReader(stream, encoding);
		return await reader.ReadToEndAsync();
	}
	
	private static Task WaitForExitAsync(SafeProcessHandle process, CancellationToken cancellationToken)
	{
		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var waitHandle = new ProcessWaitHandle(process);
		var registration = ThreadPool.RegisterWaitForSingleObject(waitHandle,
			static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), exited, Timeout.Infinite, true);
		
		var cancellation = cancellationToken.Register(() => exited.TrySetCanceled(cancellationToken));
		return exited.Task.ContinueWith(task =>
		{
			cancellation.Dispose();
			registration.Unregister(null);
			waitHandle.Dispose();
			return task;
		}, TaskScheduler.Default).Unwrap();
	}
	
	private static string BuildCommandLine(string executablePath, IEnumerable<string> arguments)
	{
		var builder = new StringBuilder();
		AppendArgument(builder, executablePath);
		foreach (var argument in arguments)
		{
			builder.Append(' ');
			AppendArgument(builder, argument);
		}
		
		return builder.ToString();
	}
	
	private static void AppendArgument(StringBuilder builder, string argument)
	{
		if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
		{
			builder.Append(argument);
			return;
		}
		
		builder.Append('"');
		var backslashes = 0;
		foreach (var character in argument)
		{
			if (character == '\\')
			{
				backslashes++;
				continue;
			}
			
			builder.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
			builder.Append(character);
			backslashes = 0;
		}
		
		builder.Append('\\', backslashes * 2);
		builder.Append('"');
	}
	
	private sealed class ProcessWaitHandle : WaitHandle
	{
		public ProcessWaitHandle(SafeProcessHandle process)
		{
			var current = GetCurrentProcess();
			if (!DuplicateHandle(current, process, current, out var duplicate, 0, false, 2))
				throw new Win32Exception();
			
			SafeWaitHandle = duplicate;
		}
	}
	
	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfo
	{
		public int Size;
		public nint Reserved;
		public nint Desktop;
		public nint Title;
		public int X;
		public int Y;
		public int XSize;
		public int YSize;
		public int XCountChars;
		public int YCountChars;
		public int FillAttribute;
		public int Flags;
		public short ShowWindow;
		public short Reserved2Size;
		public nint Reserved2;
		public nint StandardInput;
		public nint StandardOutput;
		public nint StandardError;
	}
	
	[StructLayout(LayoutKind.Sequential)]
	private unsafe struct StartupInfoEx
	{
		public StartupInfo StartupInfo;
		public void* AttributeList;
	}
	
	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessInformation
	{
		public nint Process;
		public nint Thread;
		public int ProcessId;
		public int ThreadId;
	}
	
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, void* attributes,
		uint size);
	
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
	
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool InitializeProcThreadAttributeList(void* list, int count, uint flags, ref nuint size);
	
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool UpdateProcThreadAttribute(void* list, uint flags, nint attribute, void* value,
		nuint size, void* previousValue, nuint* returnSize);
	
	[LibraryImport("kernel32.dll")]
	private static unsafe partial void DeleteProcThreadAttributeList(void* list);
	
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool CreateProcessW(char* applicationName, char* commandLine, void* processAttributes,
		void* threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
		void* environment, char* currentDirectory, StartupInfoEx* startupInfo, out ProcessInformation information);
	
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
	
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);
	
	[LibraryImport("kernel32.dll")]
	private static partial nint GetCurrentProcess();
	
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DuplicateHandle(nint sourceProcess, SafeProcessHandle source, nint targetProcess,
		out SafeWaitHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
