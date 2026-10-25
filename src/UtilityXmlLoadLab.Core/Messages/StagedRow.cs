using UtilityXmlLoadLab.Core.Mapping;

namespace UtilityXmlLoadLab.Core.Messages;

/// <summary>One typed row for a staging table. Values are in the order of <see cref="StagingTable.Columns"/>.</summary>
public sealed class StagedRow
{
	public StagingTable Table { get; }
	public object?[] Values { get; }

	public StagedRow(StagingTable table, object?[] values)
	{
		Table = table;
		Values = values;
	}
}
