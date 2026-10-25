namespace UtilityXmlLoadLab.Business.Generation;

/// <summary>What was done to a generated message. Written to dbo.TestManifest.FaultKind.</summary>
public enum EFaultKind
{
	None,

	/// <summary>The XML is cut off, so it is not well-formed. Must fail.</summary>
	Malformed,

	/// <summary>A required element was removed. Must fail.</summary>
	MissingRequired,

	/// <summary>A date or decimal has an invalid value. Must fail.</summary>
	BadValue,

	/// <summary>A valid message whose contract overlaps another contract. Must pass, with a warning finding.</summary>
	OverlapContract,

	/// <summary>A valid message installing a meter that is still installed elsewhere. Must pass, with a warning finding.</summary>
	MeterDoubleInstall
}
