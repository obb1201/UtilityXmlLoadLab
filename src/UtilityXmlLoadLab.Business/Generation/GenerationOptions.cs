namespace UtilityXmlLoadLab.Business.Generation;

/// <summary>Settings for the synthetic message generator.</summary>
public sealed class GenerationOptions
{
	public int MessageCount { get; }
	public int Seed { get; }

	/// <summary>Share of messages for EACH technical fault: malformed, missing required, bad value, duplicate.</summary>
	public double FaultRate { get; }

	/// <summary>Share of messages for EACH business-rule fault: overlapping contract, meter installed twice.</summary>
	public double BusinessFaultRate { get; }

	public GenerationOptions(int messageCount, int seed, double faultRate, double businessFaultRate)
	{
		MessageCount = messageCount;
		Seed = seed;
		FaultRate = faultRate;
		BusinessFaultRate = businessFaultRate;
	}
}
