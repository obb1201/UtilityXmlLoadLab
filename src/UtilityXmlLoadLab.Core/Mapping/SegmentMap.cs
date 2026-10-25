namespace UtilityXmlLoadLab.Core.Mapping;

/// <summary>
/// Maps one XML segment (an element at an exact path) to one row in a staging table.
/// </summary>
public sealed class SegmentMap
{
	/// <summary>Absolute element path, for example "/SapMessage/Body/Customer/Account".</summary>
	public string Path { get; }

	/// <summary>Name used in error texts, for example "Account".</summary>
	public string Label { get; }

	/// <summary>Element whose value identifies the row in error texts.</summary>
	public string KeyElementName { get; }

	public StagingTable Table { get; }

	/// <summary>One field per table column, in column order.</summary>
	public IReadOnlyList<FieldMap> Fields { get; }

	/// <summary>
	/// The message types that carry this segment. In any other message type the segment is not
	/// mapped, so the message fails on "found N segments but mapped M" (never silently dropped).
	/// </summary>
	public IReadOnlyList<string> MessageTypes { get; }

	public SegmentMap(string path, string label, string keyElementName, StagingTable table, IReadOnlyList<FieldMap> fields,
		IReadOnlyList<string> messageTypes)
	{
		Path = path;
		Label = label;
		KeyElementName = keyElementName;
		Table = table;
		Fields = fields;
		MessageTypes = messageTypes;
	}
}
