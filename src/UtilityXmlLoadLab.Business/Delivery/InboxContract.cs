namespace UtilityXmlLoadLab.Business.Delivery;

/// <summary>Values shared by the inbox service and its client.</summary>
public static class InboxContract
{
	public const string NAMESPACE = "urn:utility-xml-load-lab:inbox";
	public const string ENDPOINT_PATH = "/InboxService.svc";
	public const string OUTCOME_ACCEPTED = "Accepted";
	public const string OUTCOME_DUPLICATE = "Duplicate";
	public const string OUTCOME_REJECTED_PREFIX = "Rejected: ";
	public const int MAX_MESSAGE_ID_LENGTH = 50;
	public const int MAX_MESSAGE_BYTES = 16 * 1024 * 1024;
}
