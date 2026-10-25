namespace UtilityXmlLoadLab.DataAccess;

/// <summary>One line of the generator's test manifest (what was sent and which fault was injected).</summary>
public sealed class ManifestEntry
{
	public string MessageId { get; }
	public int ArrivalNo { get; }
	public string MessageType { get; }
	public string FaultKind { get; }
	public int ExpectedRowCount { get; }

	public ManifestEntry(string messageId, int arrivalNo, string messageType, string faultKind, int expectedRowCount)
	{
		MessageId = messageId;
		ArrivalNo = arrivalNo;
		MessageType = messageType;
		FaultKind = faultKind;
		ExpectedRowCount = expectedRowCount;
	}
}
