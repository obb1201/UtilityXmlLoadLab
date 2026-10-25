namespace UtilityXmlLoadLab.Core.Mapping;

/// <summary>
/// Maps one XML leaf element to the staging column of the same name.
/// The value comes either from the segment element itself, or from its parent segment element
/// (for example, an Account takes CustomerId from the enclosing Customer).
/// </summary>
public sealed class FieldMap
{
	public string ElementName { get; }
	public bool IsRequired { get; }
	public bool IsFromParent { get; }

	/// <summary>When not empty, the value must be one of these (checked before type rules).</summary>
	public IReadOnlyList<string> AllowedValues { get; }

	public FieldMap(string elementName, bool isRequired, bool isFromParent, IReadOnlyList<string> allowedValues)
	{
		ElementName = elementName;
		IsRequired = isRequired;
		IsFromParent = isFromParent;
		AllowedValues = allowedValues;
	}

	public static FieldMap CreateRequired(string elementName)
	{
		return new FieldMap(elementName, isRequired: true, isFromParent: false, allowedValues: Array.Empty<string>());
	}

	public static FieldMap CreateOptional(string elementName)
	{
		return new FieldMap(elementName, isRequired: false, isFromParent: false, allowedValues: Array.Empty<string>());
	}

	public static FieldMap CreateRequiredFromParent(string elementName)
	{
		return new FieldMap(elementName, isRequired: true, isFromParent: true, allowedValues: Array.Empty<string>());
	}

	public static FieldMap CreateRequiredCode(string elementName, IReadOnlyList<string> allowedValues)
	{
		return new FieldMap(elementName, isRequired: true, isFromParent: false, allowedValues: allowedValues);
	}
}
