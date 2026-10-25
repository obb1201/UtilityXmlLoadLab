using System.ServiceModel;

namespace UtilityXmlLoadLab.Business.Delivery;

/// <summary>
/// Client view of the CoreWCF inbox service. Name and namespace must match the server contract
/// (UtilityXmlLoadLab.Receiver.IInboxService); "Async" is dropped from the operation name by WCF.
/// </summary>
[ServiceContract(Name = "IInboxService", Namespace = InboxContract.NAMESPACE)]
public interface IInboxServiceClient
{
	[OperationContract]
	Task<string> SubmitMessageAsync(string messageId, string rawXml);
}
