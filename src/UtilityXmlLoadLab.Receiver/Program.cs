using CoreWCF;
using CoreWCF.Configuration;
using CoreWCF.Description;
using UtilityXmlLoadLab.Business.Delivery;
using UtilityXmlLoadLab.DataAccess;

namespace UtilityXmlLoadLab.Receiver;

/// <summary>CoreWCF host for the inbox endpoint (BasicHttpBinding, SOAP 1.1).</summary>
public static class Program
{
	public static int Main(string[] args)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
		builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);
		builder.Configuration.AddEnvironmentVariables(LabConfiguration.ENV_PREFIX);

		SqlConnectionFactory? connectionFactory = LabConfiguration.CreateConnectionFactory(builder.Configuration, out string? problem);
		if (connectionFactory == null)
		{
			Console.Error.WriteLine("Error: " + problem);
			return 2;
		}

		builder.Services.AddSingleton(connectionFactory);
		builder.Services.AddSingleton<InboxRepository>();
		builder.Services.AddSingleton<InboxService>();
		builder.Services.AddServiceModelServices();
		builder.Services.AddServiceModelMetadata();

		WebApplication app = builder.Build();

		app.UseServiceModel(serviceBuilder =>
		{
			serviceBuilder.AddService<InboxService>();
			serviceBuilder.AddServiceEndpoint<InboxService, IInboxService>(CreateBinding(), InboxContract.ENDPOINT_PATH);

			ServiceMetadataBehavior metadata = app.Services.GetRequiredService<ServiceMetadataBehavior>();
			metadata.HttpGetEnabled = true;
		});

		app.MapGet("/", () => "UtilityXmlLoadLab inbox receiver. SOAP endpoint: " + InboxContract.ENDPOINT_PATH + " (WSDL: ?wsdl)");
		app.Run();
		return 0;
	}

	private static BasicHttpBinding CreateBinding()
	{
		BasicHttpBinding binding = new BasicHttpBinding();
		binding.MaxReceivedMessageSize = InboxContract.MAX_MESSAGE_BYTES;
		binding.MaxBufferSize = InboxContract.MAX_MESSAGE_BYTES;
		binding.ReaderQuotas.MaxStringContentLength = InboxContract.MAX_MESSAGE_BYTES;
		binding.ReaderQuotas.MaxArrayLength = InboxContract.MAX_MESSAGE_BYTES;
		return binding;
	}
}
