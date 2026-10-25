namespace UtilityXmlLoadLab.Core.Mapping;

/// <summary>One typed column of a staging table (excluding MessageId and BatchId).</summary>
public sealed class StagingColumn
{
	public string Name { get; }
	public EValueType ValueType { get; }

	/// <summary>Maximum length for text columns; 0 for other types.</summary>
	public int MaxLength { get; }

	public StagingColumn(string name, EValueType valueType, int maxLength)
	{
		Name = name;
		ValueType = valueType;
		MaxLength = maxLength;
	}

	public static StagingColumn CreateText(string name, int maxLength)
	{
		return new StagingColumn(name, EValueType.Text, maxLength);
	}

	public static StagingColumn CreateTyped(string name, EValueType valueType)
	{
		return new StagingColumn(name, valueType, 0);
	}
}
