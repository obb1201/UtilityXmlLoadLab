using CoreWCF;
using UtilityXmlLoadLab.Business.Delivery;

namespace UtilityXmlLoadLab.Receiver;

/// <summary>The inbox endpoint: one operation that stores the raw XML and returns.</summary>
[ServiceContract(Name = "IInboxService", Namespace = InboxContract.NAMESPACE)]
public interface IInboxService
{
	/// <returns>"Accepted", "Duplicate" (already received; still a success), or "Rejected: reason".</returns>
	[OperationContract]
	Task<string> SubmitMessageAsync(string messageId, string rawXml);
}
