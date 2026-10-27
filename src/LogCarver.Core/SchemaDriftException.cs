namespace LogCarver.Core;

/// <summary>
/// Thrown when a row's LSN predates a detected schema-changing DDL,
/// meaning the current schema may not describe how this row was
/// physically laid out when it was written - decoding it anyway can
/// silently produce a plausible-looking but wrong value with no other
/// error signal. Never suppress this to "decode more".
/// </summary>
public sealed class SchemaDriftException(string recordLsn, string boundaryLsn)
    : Exception($"Record at LSN {recordLsn} predates a detected schema-changing DDL at LSN {boundaryLsn}; refusing to decode with the current schema.")
{
    public string RecordLsn { get; } = recordLsn;
    public string BoundaryLsn { get; } = boundaryLsn;
}
