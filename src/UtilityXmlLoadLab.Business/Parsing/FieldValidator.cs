using System.Globalization;
using UtilityXmlLoadLab.Core.Mapping;

namespace UtilityXmlLoadLab.Business.Parsing;

/// <summary>
/// Validates and converts one raw XML value. Applies exactly the same rules and error texts as
/// dbo.fn_FieldProblem (approach A), so both approaches accept and reject the same values.
/// </summary>
public static class FieldValidator
{
	private const string DATE_PATTERN = "9999-99-99";
	private const string DATE_TIME_PATTERN = "9999-99-99T99:99:99";
	private const decimal DECIMAL_18_2_LIMIT = 10000000000000000M;
	private const decimal DECIMAL_5_2_LIMIT = 1000M;
	private const int MAX_VALUE_IN_TEXT = 40;

	/// <summary>
	/// Checks a raw value against its column rules and converts it to the column type.
	/// </summary>
	/// <param name="rawValueOrNull">The element text; null when the element is missing or empty.</param>
	/// <param name="problem">null when valid, otherwise a short problem text.</param>
	/// <returns>The typed value; null when the value is missing or invalid.</returns>
	public static object? ConvertValue(string? rawValueOrNull, StagingColumn column, FieldMap field, out string? problem)
	{
		problem = null;

		if (rawValueOrNull == null)
		{
			if (field.IsRequired)
			{
				problem = column.Name + " is missing";
			}

			return null;
		}

		string value = rawValueOrNull;

		if (field.AllowedValues.Count > 0 && !field.AllowedValues.Contains(value))
		{
			problem = column.Name + " '" + GetLeft(value) + "' is unknown";
			return null;
		}

		switch (column.ValueType)
		{
			case EValueType.Text:
				if (value.Length > column.MaxLength)
				{
					problem = column.Name + " is longer than " + column.MaxLength.ToString(CultureInfo.InvariantCulture) + " characters";
					return null;
				}

				return value;

			case EValueType.Date:
				if (MatchesPattern(value, DATE_PATTERN)
					&& DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
				{
					return date;
				}

				problem = column.Name + " '" + GetLeft(value) + "' is not a valid date";
				return null;

			case EValueType.DateTime:
				if (MatchesPattern(value, DATE_TIME_PATTERN)
					&& DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dateTime))
				{
					return dateTime;
				}

				problem = column.Name + " '" + GetLeft(value) + "' is not a valid date-time";
				return null;

			case EValueType.Decimal18x2:
				return ConvertDecimal(value, column.Name, DECIMAL_18_2_LIMIT, out problem);

			case EValueType.Decimal5x2:
				return ConvertDecimal(value, column.Name, DECIMAL_5_2_LIMIT, out problem);

			case EValueType.Integer:
				if (ContainsOnly(value, "0123456789-")
					&& int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
				{
					return number;
				}

				problem = column.Name + " '" + GetLeft(value) + "' is not a valid integer";
				return null;

			default:
				System.Diagnostics.Debug.Fail("Unknown value type " + column.ValueType);
				problem = column.Name + " has an unknown type";
				return null;
		}
	}

	/// <summary>Returns the first 40 characters, as LEFT(@Value, 40) does in T-SQL.</summary>
	public static string GetLeft(string value)
	{
		return value.Length <= MAX_VALUE_IN_TEXT ? value : value.Substring(0, MAX_VALUE_IN_TEXT);
	}

	private static object? ConvertDecimal(string value, string columnName, decimal limit, out string? problem)
	{
		problem = null;

		decimal parsed = 0M;
		bool bIsNumber = ContainsOnly(value, "0123456789.-")
			&& decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
				CultureInfo.InvariantCulture, out parsed)
			&& Math.Abs(parsed) < limit;

		if (!bIsNumber)
		{
			problem = columnName + " '" + GetLeft(value) + "' is not a valid decimal";
			return null;
		}

		if (Math.Round(parsed, 2) != parsed)
		{
			problem = columnName + " '" + GetLeft(value) + "' has more than 2 decimal places";
			return null;
		}

		return parsed;
	}

	/// <summary>Pattern: '9' = an ASCII digit, any other character must match exactly.</summary>
	private static bool MatchesPattern(string value, string pattern)
	{
		if (value.Length != pattern.Length)
		{
			return false;
		}

		for (int index = 0; index < pattern.Length; index++)
		{
			char expected = pattern[index];
			char actual = value[index];
			bool bIsMatch = expected == '9' ? (actual >= '0' && actual <= '9') : actual == expected;
			if (!bIsMatch)
			{
				return false;
			}
		}

		return true;
	}

	private static bool ContainsOnly(string value, string allowedCharacters)
	{
		return value.All(character => allowedCharacters.Contains(character));
	}
}
