namespace UtilityXmlLoadLab.Business.Generation;

// Small state records the generator keeps while simulating the synthetic utility world.
// Grouped in one file because they are tiny and only used by MessageGenerator.

internal sealed class RegisterState
{
	public string RegisterNo { get; }
	public string RegisterType { get; }
	public string UnitOfMeasure { get; }
	public int Decimals { get; }

	public RegisterState(string registerNo, string registerType, string unitOfMeasure, int decimals)
	{
		RegisterNo = registerNo;
		RegisterType = registerType;
		UnitOfMeasure = unitOfMeasure;
		Decimals = decimals;
	}
}

internal sealed class MeterState
{
	public string MeterId { get; }
	public string SerialNumber { get; }
	public string MeterType { get; }
	public string Manufacturer { get; }
	public DateTime InstallDate { get; }
	public IReadOnlyList<RegisterState> Registers { get; }

	public MeterState(string meterId, string serialNumber, string meterType, string manufacturer, DateTime installDate,
		IReadOnlyList<RegisterState> registers)
	{
		MeterId = meterId;
		SerialNumber = serialNumber;
		MeterType = meterType;
		Manufacturer = manufacturer;
		InstallDate = installDate;
		Registers = registers;
	}
}

internal sealed class InstallationState
{
	public string InstallationId { get; }
	public string PremiseId { get; }
	public string Division { get; }
	public string RateCategory { get; }
	public DateTime ValidFrom { get; }
	public MeterState CurrentMeter { get; set; }

	public InstallationState(string installationId, string premiseId, string division, string rateCategory, DateTime validFrom,
		MeterState currentMeter)
	{
		InstallationId = installationId;
		PremiseId = premiseId;
		Division = division;
		RateCategory = rateCategory;
		ValidFrom = validFrom;
		CurrentMeter = currentMeter;
	}
}

internal sealed class ContractState
{
	public string ContractId { get; }
	public string AccountId { get; }
	public string CustomerId { get; }
	public string PremiseId { get; }
	public InstallationState Installation { get; }
	public DateTime StartDate { get; }

	public ContractState(string contractId, string accountId, string customerId, string premiseId, InstallationState installation,
		DateTime startDate)
	{
		ContractId = contractId;
		AccountId = accountId;
		CustomerId = customerId;
		PremiseId = premiseId;
		Installation = installation;
		StartDate = startDate;
	}
}
