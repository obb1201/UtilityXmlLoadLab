namespace UtilityXmlLoadLab.Core.Messages;

/// <summary>Validation result of one message arrival. Matches LoadEvidence_Message.ValidationStatus.</summary>
public enum EValidationStatus
{
	Passed,
	Failed,
	Duplicate
}
