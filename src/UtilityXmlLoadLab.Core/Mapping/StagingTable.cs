namespace UtilityXmlLoadLab.Core.Mapping;

/// <summary>
/// A staging table. Every staging table starts with MessageId and BatchId, followed by
/// <see cref="Columns"/> in the same order as in sql/010_Staging_Tables.sql.
/// </summary>
public sealed class StagingTable
{
	public string Name { get; }
	public string QualifiedName { get; }
	public IReadOnlyList<StagingColumn> Columns { get; }

	public StagingTable(string name, IReadOnlyList<StagingColumn> columns)
	{
		Name = name;
		QualifiedName = "stg." + name;
		Columns = columns;
	}

	/// <returns>The column index within <see cref="Columns"/>, or -1 when the table has no such column.</returns>
	public int GetColumnIndex(string columnName)
	{
		for (int index = 0; index < Columns.Count; index++)
		{
			if (Columns[index].Name == columnName)
			{
				return index;
			}
		}

		return -1;
	}
}
