using SwarmBender.Core.Abstractions;

namespace SwarmBender.Cli;

public class ConsoleOutput: IOutput
{
    public void Info(string message)    => Console.WriteLine(message);
    public void Success(string message) => Console.WriteLine(message);
    public void Warn(string message)    => Console.WriteLine("[warn] " + message);
    public void Error(string message)   => Console.Error.WriteLine("[error] " + message);

    public void WriteKeyValue(string key, string value, bool mask = false)
    {
        Console.WriteLine($"{key}={(mask ? "****" : value)}");
    }
}