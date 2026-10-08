namespace LogCarver.Core;

/// <summary>
/// A decoded value for a CLR user-defined-type column (system_type_id 240
/// - geography, geometry, hierarchyid, ... all report this same generic
/// marker, so the real type name has to travel alongside the bytes rather
/// than being inferred from system_type_id alone). These types serialize
/// to plain bytes in-row exactly like VARBINARY does, but unlike a plain
/// VARBINARY value they need an explicit CONVERT(typename, 0x...) to turn
/// back into their real type - see SqlStatementBuilder's own doc comment
/// for the literal/WHERE-clause details, including why geography/geometry
/// specifically can't use a plain "=" comparison.
/// </summary>
public sealed record SqlClrBinaryValue(byte[] Bytes, string TypeName);
