using System.Text.Json;
using Pitstop.Models;

namespace Pitstop.Services;

/// <summary>Driver history: every install and rollback, newest first (history.json).</summary>
public sealed class HistoryService
{
    private readonly object _gate = new();
    private List<HistoryEntry> _entries = new();

    public event Action? Changed;

    public HistoryService()
    {
        try
        {
            if (File.Exists(Paths.History))
                _entries = JsonSerializer.Deserialize(File.ReadAllText(Paths.History), PitstopJson.Default.ListHistoryEntry) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("history load", ex);
        }
    }

    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_gate) return _entries.OrderByDescending(e => e.Date).ToList(); }
    }

    public void Add(HistoryEntry e)
    {
        lock (_gate)
        {
            _entries.Add(e);
            if (_entries.Count > 500) _entries = _entries.OrderByDescending(x => x.Date).Take(500).ToList();
            try
            {
                Files.WriteAtomic(Paths.History, JsonSerializer.Serialize(_entries, PitstopJson.Default.ListHistoryEntry));
            }
            catch (Exception ex)
            {
                Log.Error("history save", ex);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>The branch Pitstop last installed for this version, if it installed it.</summary>
    public string? BranchFor(string version)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Version == version && e.Result == "success" && e.Branch.Length > 0)
                .OrderByDescending(e => e.Date).Select(e => e.Branch).FirstOrDefault();
        }
    }
}
