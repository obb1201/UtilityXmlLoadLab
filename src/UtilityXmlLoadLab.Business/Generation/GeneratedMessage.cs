namespace UtilityXmlLoadLab.Business.Generation;

/// <summary>One generated message (or a duplicate arrival of one).</summary>
public sealed class GeneratedMessage
{
	public string MessageId { get; }
	public string MessageType { get; }
	public DateTime CreatedAt { get; }

	/// <summary>Number of segments in the clean message (= rows it should stage).</summary>
	public int ExpectedRowCount { get; }

	public string Xml { get; set; }
	public EFaultKind FaultKind { get; set; }

	/// <summary>1 = first arrival; 2+ = duplicate arrival.</summary>
	public int ArrivalNo { get; }

	/// <summary>When set, this message must arrive just before that message (out-of-order edge case).</summary>
	public string? ArriveBeforeMessageIdOrNull { get; set; }

	public GeneratedMessage(string messageId, string messageType, DateTime createdAt, int expectedRowCount, string xml,
		EFaultKind faultKind, int arrivalNo)
	{
		MessageId = messageId;
		MessageType = messageType;
		CreatedAt = createdAt;
		ExpectedRowCount = expectedRowCount;
		Xml = xml;
		FaultKind = faultKind;
		ArrivalNo = arrivalNo;
	}

	public GeneratedMessage CreateDuplicateArrival(int arrivalNo)
	{
		return new GeneratedMessage(MessageId, MessageType, CreatedAt, ExpectedRowCount, Xml, FaultKind, arrivalNo);
	}
}
