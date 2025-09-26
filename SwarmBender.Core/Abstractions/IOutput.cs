namespace SwarmBender.Core.Abstractions;

public interface IOutput
{
    void Info(string message);
    void Success(string message);
    void Warn(string message);
    void Error(string message);

    void WriteKeyValue(string key, string value, bool mask = false);
}