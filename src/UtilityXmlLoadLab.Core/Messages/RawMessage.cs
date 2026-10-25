namespace UtilityXmlLoadLab.Core.Messages;

/// <summary>One message as stored in dbo.Inbox.</summary>
public sealed class RawMessage
{
	public string MessageId { get; }
	public DateTime ReceivedAt { get; }
	public string RawXml { get; }

	public RawMessage(string messageId, DateTime receivedAt, string rawXml)
	{
		MessageId = messageId;
		ReceivedAt = receivedAt;
		RawXml = rawXml;
	}
}
