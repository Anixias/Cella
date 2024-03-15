using Cella.Analysis.Text;

namespace CellaConsole;

public abstract class CompilationSource
{
	public abstract Task<IBuffer> GetBuffer();
	
	public sealed class File : CompilationSource
	{
		public string FilePath { get; }
		
		public File(string filePath)
		{
			FilePath = filePath;
		}

		public override async Task<IBuffer> GetBuffer()
		{
			var source = await System.IO.File.ReadAllTextAsync(FilePath);
			return new StringBuffer(source);
		}
	}
	
	public sealed class RawText : CompilationSource
	{
		private readonly string text;
		
		public RawText(string text)
		{
			this.text = text;
		}

		public override Task<IBuffer> GetBuffer() => Task.FromResult<IBuffer>(new StringBuffer(text));
	}
}