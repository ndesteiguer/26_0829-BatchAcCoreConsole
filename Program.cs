using BatchAcCore.Core;

return await BatchRunner.RunAsync(args, new ConsoleBatchOutput());

file sealed class ConsoleBatchOutput : IBatchOutput
{
    public void WriteLine(string message) => Console.WriteLine(message);
    public void WriteError(string message) => Console.Error.WriteLine(message);
}
