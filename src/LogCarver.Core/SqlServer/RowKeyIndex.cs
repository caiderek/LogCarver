namespace LogCarver.Core.SqlServer;

/// <summary>
/// Decides which tracked row a log record belongs to, tying together the two
/// kinds of evidence a record can carry (see <see cref="RowIdentity"/>):
///   - the key hash, present on most INSERT and UPDATE records of a clustered
///     row but NOT on all of them (for instance rows inserted after a large
///     statement escalated to a table lock carry no lock information), and
///   - the (page, slot) it was logged at, which is what every record has but
///     which moves when a page splits or other rows are inserted.
///
/// A row is tracked under whichever key it was first seen with. When a record
/// with a hash arrives for a slot that is already tracked (and that tracked row
/// has no different hash), the hash is linked to it, so later records follow
/// the row through slot changes. A hash seen for the first time at a slot that
/// is not tracked starts a new row. The index is only a pointer: the caller
/// still verifies the logged old bytes against the row's known image before
/// applying anything, so a wrong link can only cause a refusal.
/// </summary>
public sealed class RowKeyIndex
{
    private readonly Dictionary<string, string> _keyByHash = [];
    private readonly Dictionary<string, string> _hashByKey = [];
    private readonly Dictionary<string, string> _keyBySlot = [];

    private static string SlotOf(LogRecord record) => $"P:{record.PageId}:{record.SlotId}";

    public string Resolve(LogRecord record)
    {
        string slot = SlotOf(record);
        string? hash = RowIdentity.KeyHash(RowIdentity.LockInformation(record));

        if (hash is not null)
        {
            if (_keyByHash.TryGetValue(hash, out var known))
                return known;

            if (_keyBySlot.TryGetValue(slot, out var atSlot) &&
                (!_hashByKey.TryGetValue(atSlot, out var otherHash) || otherHash == hash))
                return atSlot;

            return $"H:{record.AllocUnitName}:{hash}";
        }

        return _keyBySlot.TryGetValue(slot, out var bySlot) ? bySlot : slot;
    }

    /// <summary>Record that <paramref name="record"/> was seen as the row <paramref name="key"/>.</summary>
    public void Observe(LogRecord record, string key)
    {
        _keyBySlot[SlotOf(record)] = key;
        string? hash = RowIdentity.KeyHash(RowIdentity.LockInformation(record));
        if (hash is not null)
        {
            _keyByHash[hash] = key;
            _hashByKey[key] = hash;
        }
    }

    /// <summary>A row was removed from this slot.</summary>
    public void ForgetSlot(LogRecord record) => _keyBySlot.Remove(SlotOf(record));
}
