namespace UtilityXmlLoadLab.Business.Parsing;

/// <summary>
/// A light record of one XML element seen by the streaming parser: its path, its first text,
/// and the first text of each child element (like "(Child/text())[1]" in T-SQL).
/// </summary>
internal sealed class ElementFrame
{
	public string Name { get; }
	public string Path { get; }
	public ElementFrame? ParentOrNull { get; }

	/// <summary>First text node directly inside this element; null when there is none.</summary>
	public string? TextOrNull { get; private set; }

	private Dictionary<string, string>? _childTexts;

	public ElementFrame(string name, string path, ElementFrame? parentOrNull)
	{
		Name = name;
		Path = path;
		ParentOrNull = parentOrNull;
	}

	public void AddText(string text)
	{
		if (TextOrNull == null)
		{
			TextOrNull = text;
		}
	}

	/// <summary>Keeps the first text seen for each child element name.</summary>
	public void AddChildText(string childName, string text)
	{
		_childTexts ??= new Dictionary<string, string>(StringComparer.Ordinal);
		_childTexts.TryAdd(childName, text);
	}

	/// <returns>null when no child element with this name has text.</returns>
	public string? GetChildTextOrNull(string childName)
	{
		if (_childTexts == null)
		{
			return null;
		}

		return _childTexts.TryGetValue(childName, out string? text) ? text : null;
	}
}
