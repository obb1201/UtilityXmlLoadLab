using System.Data;
using System.Globalization;
using System.Text;

namespace UtilityXmlLoadLab.Business.Hosting;

/// <summary>Formats a DataTable as a plain-text table for the console.</summary>
public static class TablePrinter
{
	private const int MAX_CELL_WIDTH = 80;

	public static string FormatTable(DataTable table)
	{
		if (table.Columns.Count == 0)
		{
			return string.Empty;
		}

		string[][] cells = new string[table.Rows.Count + 1][];
		cells[0] = table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray();

		for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
		{
			cells[rowIndex + 1] = table.Rows[rowIndex].ItemArray.Select(FormatCell).ToArray();
		}

		int[] widths = Enumerable.Range(0, table.Columns.Count)
			.Select(columnIndex => cells.Max(row => row[columnIndex].Length))
			.ToArray();

		StringBuilder text = new StringBuilder();
		for (int rowIndex = 0; rowIndex < cells.Length; rowIndex++)
		{
			text.AppendLine(string.Join("  ", cells[rowIndex].Select((cell, columnIndex) => cell.PadRight(widths[columnIndex]))).TrimEnd());
			if (rowIndex == 0)
			{
				text.AppendLine(string.Join("  ", widths.Select(width => new string('-', width))));
			}
		}

		return text.ToString();
	}

	private static string FormatCell(object? value)
	{
		string text;
		switch (value)
		{
			case null:
			case DBNull:
				text = "NULL";
				break;
			case DateTime dateTime:
				text = dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
				break;
			case byte[] bytes:
				text = "0x" + Convert.ToHexString(bytes);
				break;
			case IFormattable formattable:
				text = formattable.ToString(null, CultureInfo.InvariantCulture);
				break;
			default:
				text = value.ToString() ?? string.Empty;
				break;
		}

		return text.Length <= MAX_CELL_WIDTH ? text : text.Substring(0, MAX_CELL_WIDTH - 3) + "...";
	}
}
