namespace LogCarver.Core.SqlServer;

/// <summary>
/// Decides which tracked row a log record belongs to, tying together the two
/// kinds of evidence a record can carry (see <see cref="RowIdentity"/>):
///   - the key hash, present on most INSERT and UPDATE records of a clustered
///     row but NOT on all of them (for instance rows inserted after a large
///     statement escalated to a table lock carry no lock information), and
///   - the (page, slot) it was logged at, which every record has.
///
/// The slot is only evidence while it is still the row's slot, so the index
/// follows what the log says about slots: an INSERT into a clustered page
/// pushes every row at or after its slot up by one, a physical DELETE pulls
/// them back down (a ghosted delete leaves the row where it is), and a heap's
/// slots never move. What it cannot see - a page split moving rows to another
/// page - is not modelled, so a slot entry can still go stale; that is why
/// the caller verifies the logged old bytes against the row's known image
/// before applying anything. That check is not proof of identity for a short
/// field, so slot evidence is only ever used to attach a hash to a row that
/// has none yet, never to override a hash already known.
///
/// A row is tracked under whichever key it was first seen with: its hash if
/// it had one, otherwise a name built from where and when it first appeared.
/// </summary>
public sealed class RowKeyIndex
{
    private readonly Dictionary<string, string> _keyByHash = [];
    private readonly Dictionary<string, string> _hashByKey = [];
    private readonly Dictionary<string, Dictionary<int, string>> _slotsByPage = [];
    private readonly Dictionary<string, (string Page, int Slot)> _placeByKey = [];

    /// <summary>Where the row is believed to be now, or null if that is not known.</summary>
    public (string Page, int Slot)? PlaceOf(string key) =>
        _placeByKey.TryGetValue(key, out var place) ? place : null;

    /// <summary>The row currently believed to sit at this slot, or null.</summary>
    public string? KeyAt(string pageId, int slot) =>
        _slotsByPage.TryGetValue(pageId, out var slots) && slots.TryGetValue(slot, out var key) ? key : null;

    public string Resolve(LogRecord record)
    {
        string? hash = RowIdentity.KeyHash(RowIdentity.LockInformation(record));

        // An INSERT is a new row at that slot; the row previously at that slot
        // is about to be pushed along, it is not this one.
        if (record.Operation == "LOP_INSERT_ROWS")
        {
            if (hash is not null && _keyByHash.TryGetValue(hash, out var movedRow))
                return movedRow;
            return hash is not null
                ? $"H:{record.AllocUnitName}:{hash}"
                : $"P:{record.PageId}:{record.SlotId}@{record.Lsn}";
        }

        string? atSlot = KeyAt(record.PageId!, record.SlotId!.Value);
        if (hash is not null)
        {
            if (_keyByHash.TryGetValue(hash, out var known))
                return known;

            if (atSlot is not null && !_hashByKey.ContainsKey(atSlot))
                return atSlot;

            return $"H:{record.AllocUnitName}:{hash}";
        }

        return atSlot ?? $"P:{record.PageId}:{record.SlotId}@{record.Lsn}";
    }

    /// <summary>Record that an INSERT or UPDATE <paramref name="record"/> was seen as the row <paramref name="key"/>.</summary>
    public void Observe(LogRecord record, string key)
    {
        string page = record.PageId!;
        int slot = record.SlotId!.Value;

        if (!_slotsByPage.TryGetValue(page, out var slots))
            _slotsByPage[page] = slots = [];

        // The row may have been tracked at another slot; that entry is stale.
        if (_placeByKey.TryGetValue(key, out var old) && KeyAtIs(old.Page, old.Slot, key))
            _slotsByPage[old.Page].Remove(old.Slot);

        if (record.Operation == "LOP_INSERT_ROWS" && record.Context == "LCX_CLUSTERED")
            ShiftUp(slots, slot);

        slots[slot] = key;
        _placeByKey[key] = (page, slot);

        string? hash = RowIdentity.KeyHash(RowIdentity.LockInformation(record));
        if (hash is not null)
        {
            _keyByHash[hash] = key;
            _hashByKey[key] = hash;
        }
    }

    /// <summary>A DELETE of the row at this slot.</summary>
    public void ObserveDelete(LogRecord record)
    {
        // A ghosted row stays on the page until it is cleaned up; only a
        // physical removal changes what the slots mean.
        if (record.Context == "LCX_MARK_AS_GHOST")
            return;

        if (!_slotsByPage.TryGetValue(record.PageId!, out var slots))
            return;

        int slot = record.SlotId!.Value;
        if (slots.Remove(slot, out var removed))
            _placeByKey.Remove(removed);

        if (record.Context == "LCX_CLUSTERED")
            ShiftDown(slots, slot);
    }

    /// <summary>
    /// A page split (LOP_DELETE_SPLIT): every row of <paramref name="oldPage"/>
    /// at <paramref name="fromSlot"/> or above is moved to the freshly
    /// formatted <paramref name="newPage"/>, keeping its order, so old slot n
    /// becomes new slot n - fromSlot. The log holds no record per moved row,
    /// which is why this has to be modelled: without it every later record
    /// for a moved row is looked up at a slot that no longer means that row.
    /// Returns the keys that moved (in new-slot order) and the key an INSERT
    /// logged at the new page's slot 0 had been given, if any - that INSERT is
    /// the first moved row itself, not a new row.
    /// </summary>
    public (IReadOnlyList<string> Moved, string? PhantomAtSlotZero, string? KeyAtSlotZero)? ObserveSplit(
        string oldPage, int fromSlot, string newPage)
    {
        // A freshly formatted page holds at most the first moved row (the
        // INSERT logged at slot 0). Anything else on it means the page is not
        // the split's new page, and nothing is modelled.
        string? phantom = null;
        if (_slotsByPage.TryGetValue(newPage, out var existing))
        {
            if (existing.Keys.Any(slot => slot != 0))
                return null;
            if (existing.TryGetValue(0, out var atZero))
                phantom = atZero;
        }

        var target = _slotsByPage[newPage] = [];
        if (phantom is not null)
            _placeByKey.Remove(phantom);

        var moved = new List<string>();
        string? keyAtZero = null;
        if (_slotsByPage.TryGetValue(oldPage, out var source))
        {
            foreach (var (slot, key) in source.Where(kv => kv.Key >= fromSlot).OrderBy(kv => kv.Key).ToList())
            {
                source.Remove(slot);
                int newSlot = slot - fromSlot;
                target[newSlot] = key;
                _placeByKey[key] = (newPage, newSlot);
                moved.Add(key);
                if (newSlot == 0) keyAtZero = key;
            }
        }

        // The INSERT at slot 0 is a row on that page whatever else is known:
        // when no tracked row moved to slot 0 it stays what it was.
        if (phantom is not null && !target.ContainsKey(0))
        {
            target[0] = phantom;
            _placeByKey[phantom] = (newPage, 0);
        }
        return (moved, phantom, keyAtZero);
    }

    /// <summary>
    /// A split whose destination is not known: what the slots from
    /// <paramref name="fromSlot"/> up mean is no longer known, so they are
    /// forgotten rather than left to point at the wrong rows.
    /// </summary>
    public void ForgetFromSlot(string page, int fromSlot)
    {
        if (!_slotsByPage.TryGetValue(page, out var slots)) return;
        foreach (var (slot, key) in slots.Where(kv => kv.Key >= fromSlot).ToList())
        {
            slots.Remove(slot);
            _placeByKey.Remove(key);
        }
    }

    private bool KeyAtIs(string page, int slot, string key) =>
        _slotsByPage.TryGetValue(page, out var slots) && slots.TryGetValue(slot, out var k) && k == key;

    private void ShiftUp(Dictionary<int, string> slots, int from)
    {
        foreach (var slot in slots.Keys.Where(s => s >= from).OrderByDescending(s => s).ToList())
        {
            string key = slots[slot];
            slots.Remove(slot);
            slots[slot + 1] = key;
            if (_placeByKey.TryGetValue(key, out var place))
                _placeByKey[key] = (place.Page, slot + 1);
        }
    }

    private void ShiftDown(Dictionary<int, string> slots, int after)
    {
        foreach (var slot in slots.Keys.Where(s => s > after).OrderBy(s => s).ToList())
        {
            string key = slots[slot];
            slots.Remove(slot);
            slots[slot - 1] = key;
            if (_placeByKey.TryGetValue(key, out var place))
                _placeByKey[key] = (place.Page, slot - 1);
        }
    }
}
