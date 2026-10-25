namespace UtilityXmlLoadLab.Business.Delivery;

/// <summary>Counts of a delivery run.</summary>
public sealed class DeliveryResult
{
	public int AcceptedCount { get; }
	public int DuplicateCount { get; }
	public int RejectedCount { get; }
	public IReadOnlyList<string> Problems { get; }

	public DeliveryResult(int acceptedCount, int duplicateCount, int rejectedCount, IReadOnlyList<string> problems)
	{
		AcceptedCount = acceptedCount;
		DuplicateCount = duplicateCount;
		RejectedCount = rejectedCount;
		Problems = problems;
	}
}
