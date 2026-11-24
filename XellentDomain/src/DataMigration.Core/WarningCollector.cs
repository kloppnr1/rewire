namespace DataMigration.Core;

public record ValidationEntry(
    string Severity,     // Error, Warning, Info
    string EntityType,   // Customer, Payer, MP
    string EntityId,     // e.g. "50035", "571313108401375003"
    string Pattern,      // e.g. "missing Email", "no AddressIdentifier (DAR) found"
    string SettlField,   // e.g. "Email", "AddressIdentifier"
    string XellentField, // e.g. "Email / DirECommunication"
    string? XellentValue,// Original value from Xellent (null if empty)

    /// <summary>
    /// False for findings that belong only in the structured report table, not in the run log.
    /// A finding that says "go fix this in Xellent" is a worksheet item — repeating it as a
    /// free-text warning line puts the same thing in two places, once grouped into
    /// unreadability (long ids are masked to '#') and once unformatted. Default true keeps
    /// every existing entry logging as before.
    /// </summary>
    bool LogAsMessage = true
);

public interface IWarningCollector
{
    void AddInfo(string info);
    void AddWarning(string warning, int count = 1);
    void AddError(string error, int count = 1);
    void AddValidationEntry(ValidationEntry entry);

    IReadOnlyList<string> GetInfos();
    IReadOnlyList<string> GetWarnings();
    IReadOnlyList<string> GetErrors();
    IReadOnlyList<ValidationEntry> GetValidationEntries();

    int WarningCount { get; }
    int ErrorCount { get; }
}

/// <summary>
/// Thread-safe warning/error collector. One instance per migration run so
/// concurrent runs (e.g. background live migration + HTTP dry-run in the
/// same DataMigration.Api process) never share state.
/// </summary>
public class WarningCollector : IWarningCollector
{
    private readonly Lock _lock = new();
    private readonly List<string> _infos = new();
    private readonly List<string> _warnings = new();
    private readonly List<string> _errors = new();
    private readonly List<ValidationEntry> _validationEntries = new();
    private int _warningCount;
    private int _errorCount;

    public void AddInfo(string info)
    {
        lock (_lock) _infos.Add(info);
    }

    public void AddWarning(string warning, int count = 1)
    {
        lock (_lock)
        {
            _warnings.Add(warning);
            _warningCount += count;
        }
    }

    public void AddError(string error, int count = 1)
    {
        lock (_lock)
        {
            _errors.Add(error);
            _errorCount += count;
        }
    }

    public void AddValidationEntry(ValidationEntry entry)
    {
        lock (_lock) _validationEntries.Add(entry);
    }

    public IReadOnlyList<string> GetInfos() { lock (_lock) return _infos.ToList().AsReadOnly(); }
    public IReadOnlyList<string> GetWarnings() { lock (_lock) return _warnings.ToList().AsReadOnly(); }
    public IReadOnlyList<string> GetErrors() { lock (_lock) return _errors.ToList().AsReadOnly(); }
    public IReadOnlyList<ValidationEntry> GetValidationEntries() { lock (_lock) return _validationEntries.ToList().AsReadOnly(); }

    public int WarningCount { get { lock (_lock) return _warningCount; } }
    public int ErrorCount { get { lock (_lock) return _errorCount; } }
}
