using System.Globalization;

namespace UtilityXmlLoadLab.Business.Hosting;

/// <summary>
/// Minimal "--name value" / "--flag" parser for the console hosts. Unknown or malformed options are
/// collected in <see cref="Problems"/> so the host can report them at the boundary.
/// </summary>
public sealed class CommandLineArgs
{
	public IReadOnlyList<string> Positionals { get; }
	public List<string> Problems { get; } = new List<string>();

	private readonly Dictionary<string, string?> _options;

	private CommandLineArgs(IReadOnlyList<string> positionals, Dictionary<string, string?> options)
	{
		Positionals = positionals;
		_options = options;
	}

	public static CommandLineArgs Parse(string[] args)
	{
		List<string> positionals = new List<string>();
		Dictionary<string, string?> options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

		for (int index = 0; index < args.Length; index++)
		{
			string arg = args[index];
			if (!arg.StartsWith("--", StringComparison.Ordinal))
			{
				positionals.Add(arg);
				continue;
			}

			string name = arg.Substring(2);
			bool bHasValue = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal);
			options[name] = bHasValue ? args[++index] : null;
		}

		return new CommandLineArgs(positionals, options);
	}

	public bool HasFlag(string name)
	{
		return _options.ContainsKey(name);
	}

	public string GetText(string name, string defaultValue)
	{
		return _options.TryGetValue(name, out string? value) && value != null ? value : defaultValue;
	}

	/// <returns>null when the option is not given.</returns>
	public string? GetTextOrNull(string name)
	{
		return _options.TryGetValue(name, out string? value) ? value : null;
	}

	public int GetInt(string name, int defaultValue, int minimum)
	{
		if (!_options.TryGetValue(name, out string? value) || value == null)
		{
			return defaultValue;
		}

		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < minimum)
		{
			Problems.Add("--" + name + " must be a whole number of at least " + minimum.ToString(CultureInfo.InvariantCulture) + ".");
			return defaultValue;
		}

		return number;
	}

	public double GetRate(string name, double defaultValue)
	{
		if (!_options.TryGetValue(name, out string? value) || value == null)
		{
			return defaultValue;
		}

		if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) || rate < 0 || rate > 0.2)
		{
			Problems.Add("--" + name + " must be a number between 0 and 0.2 (for example 0.01).");
			return defaultValue;
		}

		return rate;
	}

	/// <summary>Comma-separated whole numbers, for example "1,4,8".</summary>
	public IReadOnlyList<int> GetIntList(string name, IReadOnlyList<int> defaultValue)
	{
		if (!_options.TryGetValue(name, out string? value) || value == null)
		{
			return defaultValue;
		}

		List<int> numbers = new List<int>();
		foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < 1)
			{
				Problems.Add("--" + name + " must be a comma-separated list of whole numbers of at least 1.");
				return defaultValue;
			}

			numbers.Add(number);
		}

		return numbers;
	}
}
