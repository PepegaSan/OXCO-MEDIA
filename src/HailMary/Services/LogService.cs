namespace HailMary.Services;

public sealed class LogService
{
    public event Action<string>? LineAppended;

    public void Info(string message) => Append("INFO", message);

    public void Error(string message) => Append("ERROR", message);

    public void Success(string message) => Append("OK", message);

    private void Append(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {level}: {message}";
        UiDispatcher.Run(() => LineAppended?.Invoke(line));
    }
}
