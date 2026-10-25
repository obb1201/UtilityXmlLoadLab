using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace UtilityXmlLoadLab.Business.Benchmarking;

/// <summary>
/// Samples the CPU use of the SQL Server container with "docker stats" while a run is going.
/// 100% = one full CPU core. Sampling is best effort: when docker is not available, it reports
/// no samples instead of failing the benchmark.
/// </summary>
public sealed class DockerCpuSampler
{
	public IReadOnlyList<double> Samples => _samples;
	public string? ProblemOrNull { get; private set; }

	private readonly string _containerName;
	private readonly List<double> _samples = new List<double>();
	private CancellationTokenSource? _stopSource;
	private Task? _samplingTask;

	public DockerCpuSampler(string containerName)
	{
		_containerName = containerName;
	}

	public void Start()
	{
		_samples.Clear();
		_stopSource = new CancellationTokenSource();
		CancellationToken token = _stopSource.Token;
		_samplingTask = Task.Run(async () => await SampleUntilStoppedAsync(token));
	}

	public async Task StopAsync()
	{
		if (_stopSource == null || _samplingTask == null)
		{
			return;
		}

		_stopSource.Cancel();
		await _samplingTask;
		_stopSource.Dispose();
		_stopSource = null;
	}

	private async Task SampleUntilStoppedAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			double? sample = await ReadCpuPercentAsync();
			if (sample == null)
			{
				return;
			}

			lock (_samples)
			{
				_samples.Add(sample.Value);
			}
		}
	}

	/// <returns>null when docker could not be asked (the problem is kept in <see cref="ProblemOrNull"/>).</returns>
	private async Task<double?> ReadCpuPercentAsync()
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("docker");
		startInfo.ArgumentList.Add("stats");
		startInfo.ArgumentList.Add("--no-stream");
		startInfo.ArgumentList.Add("--format");
		startInfo.ArgumentList.Add("{{.CPUPerc}}");
		startInfo.ArgumentList.Add(_containerName);
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;

		try
		{
			using Process? process = Process.Start(startInfo);
			if (process == null)
			{
				ProblemOrNull = "docker could not be started.";
				return null;
			}

			string output = await process.StandardOutput.ReadToEndAsync();
			await process.WaitForExitAsync();
			string text = output.Trim().TrimEnd('%');

			if (process.ExitCode != 0 || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
			{
				ProblemOrNull = "docker stats returned no CPU value for container " + _containerName + ".";
				return null;
			}

			return percent;
		}
		catch (Win32Exception exception)
		{
			ProblemOrNull = "docker is not available: " + exception.Message;
			return null;
		}
	}
}
