using System.Data;
using Microsoft.Data.SqlClient;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>Reads every result set of a command into DataTables (for reports and printing).</summary>
public static class ResultSetReader
{
	public static async Task<IReadOnlyList<DataTable>> ReadAllResultSetsAsync(SqlCommand command, CancellationToken cancellationToken)
	{
		List<DataTable> tables = new List<DataTable>();

		using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (!reader.IsClosed)
		{
			DataTable table = new DataTable();
			table.Load(reader);
			tables.Add(table);
		}

		return tables;
	}
}
