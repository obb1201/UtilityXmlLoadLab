namespace UtilityXmlLoadLab.Core.Messages;

/// <summary>
/// The outcome of parsing and validating one message: its evidence values and, when it passed,
/// the typed rows for staging (all-or-nothing: a failed message has no rows).
/// </summary>
public sealed class MessageResult
{
	public string MessageId { get; }
	public DateTime ReceivedAt { get; }
	public byte[] RawXmlHash { get; }

	/// <summary>null when the header has no MessageType.</summary>
	public string? MessageType { get; }

	/// <summary>null when the XML is not well-formed.</summary>
	public int? ExpectedRowCount { get; }

	public EValidationStatus Status { get; }

	/// <summary>null when the message passed.</summary>
	public string? ErrorDetail { get; }

	public IReadOnlyList<StagedRow> Rows { get; }

	public MessageResult(string messageId, DateTime receivedAt, byte[] rawXmlHash, string? messageType,
		int? expectedRowCount, EValidationStatus status, string? errorDetail, IReadOnlyList<StagedRow> rows)
	{
		MessageId = messageId;
		ReceivedAt = receivedAt;
		RawXmlHash = rawXmlHash;
		MessageType = messageType;
		ExpectedRowCount = expectedRowCount;
		Status = status;
		ErrorDetail = errorDetail;
		Rows = rows;
	}
}
