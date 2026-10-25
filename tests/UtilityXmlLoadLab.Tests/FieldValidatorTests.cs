using UtilityXmlLoadLab.Business.Parsing;
using UtilityXmlLoadLab.Core.Mapping;

namespace UtilityXmlLoadLab.Tests;

/// <summary>The same values must be accepted and rejected as by dbo.fn_FieldProblem (approach A).</summary>
public class FieldValidatorTests
{
	private static readonly FieldMap REQUIRED = FieldMap.CreateRequired("Field");
	private static readonly FieldMap OPTIONAL = FieldMap.CreateOptional("Field");

	[Theory]
	[InlineData("2024-01-31", true)]
	[InlineData("2023-02-30", false)]
	[InlineData("2024-1-5", false)]
	[InlineData("31/12/2023", false)]
	[InlineData("2023-13-01", false)]
	public void ConvertValue_Date_AcceptsOnlyStrictValidDates(string value, bool bExpectedValid)
	{
		StagingColumn column = StagingColumn.CreateTyped("StartDate", EValueType.Date);

		object? converted = FieldValidator.ConvertValue(value, column, REQUIRED, out string? problem);

		Assert.Equal(bExpectedValid, problem == null);
		Assert.Equal(bExpectedValid, converted is DateTime);
	}

	[Theory]
	[InlineData("12.50", null)]
	[InlineData("-3", null)]
	[InlineData("12,50", "Amount '12,50' is not a valid decimal")]
	[InlineData("1.2.3", "Amount '1.2.3' is not a valid decimal")]
	[InlineData("9e5", "Amount '9e5' is not a valid decimal")]
	[InlineData("12.345", "Amount '12.345' has more than 2 decimal places")]
	[InlineData("99999999999999999", "Amount '99999999999999999' is not a valid decimal")]
	public void ConvertValue_Decimal_GivesSameTextAsTSql(string value, string? expectedProblem)
	{
		StagingColumn column = StagingColumn.CreateTyped("Amount", EValueType.Decimal18x2);

		FieldValidator.ConvertValue(value, column, REQUIRED, out string? problem);

		Assert.Equal(expectedProblem, problem);
	}

	[Fact]
	public void ConvertValue_MissingRequired_ReportsMissing()
	{
		StagingColumn column = StagingColumn.CreateText("AccountId", 20);

		FieldValidator.ConvertValue(null, column, REQUIRED, out string? problem);

		Assert.Equal("AccountId is missing", problem);
	}

	[Fact]
	public void ConvertValue_MissingOptional_IsValidNull()
	{
		StagingColumn column = StagingColumn.CreateTyped("EndDate", EValueType.Date);

		object? converted = FieldValidator.ConvertValue(null, column, OPTIONAL, out string? problem);

		Assert.Null(problem);
		Assert.Null(converted);
	}

	[Fact]
	public void ConvertValue_TextTooLong_ReportsLength()
	{
		StagingColumn column = StagingColumn.CreateText("Country", 2);

		FieldValidator.ConvertValue("USA", column, REQUIRED, out string? problem);

		Assert.Equal("Country is longer than 2 characters", problem);
	}

	[Fact]
	public void ConvertValue_UnknownCode_ReportsUnknown()
	{
		StagingColumn column = StagingColumn.CreateText("EventType", 10);
		FieldMap field = FieldMap.CreateRequiredCode("EventType", StagingMap.EVENT_TYPES);

		FieldValidator.ConvertValue("MOVE_UP", column, field, out string? problem);

		Assert.Equal("EventType 'MOVE_UP' is unknown", problem);
	}
}
