namespace UtilityXmlLoadLab.Core.Mapping;

/// <summary>The data type of a staging column. Matches the type codes used by dbo.fn_FieldProblem.</summary>
public enum EValueType
{
	Text,
	Date,
	DateTime,
	Decimal18x2,
	Decimal5x2,
	Integer
}
