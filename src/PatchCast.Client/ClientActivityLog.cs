namespace PatchCast.Client;

internal sealed class ClientActivityLog
{
    private const int MaximumEntries = 2000;
    private readonly object syncRoot = new();
    private readonly List<string> entries = [];

    public event Action<string>? EntryAdded;

    public void Write(string message)
    {
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (syncRoot)
        {
            entries.Add(entry);
            if (entries.Count > MaximumEntries)
                entries.RemoveRange(0, entries.Count - MaximumEntries);
        }
        EntryAdded?.Invoke(entry);
    }

    public string[] Snapshot()
    {
        lock (syncRoot)
            return entries.ToArray();
    }
}
