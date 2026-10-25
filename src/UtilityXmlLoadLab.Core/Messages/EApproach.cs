namespace UtilityXmlLoadLab.Core.Messages;

/// <summary>The two loading approaches being compared.</summary>
public enum EApproach
{
	/// <summary>T-SQL shredding (usp_ShredBatch).</summary>
	TSql,

	/// <summary>.NET XmlReader parsing + SqlBulkCopy.</summary>
	DotNet
}
