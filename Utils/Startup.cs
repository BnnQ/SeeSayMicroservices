using System.IO;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeeSayMicroservices.Utils;
using SeeSayMicroservices.Configuration;
using SeeSayMicroservices.Services;
using SeeSayMicroservices.Services.Abstractions;
using SeeSayMicroservices.Services.MapperProfiles;

[assembly: FunctionsStartup(typeof(Startup))]
namespace SeeSayMicroservices.Utils;

public class Startup : FunctionsStartup
{
    public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
    {
        builder.ConfigurationBuilder.AddJsonFile(Path.Combine(builder.GetContext()
            .ApplicationRootPath, "appsettings.json"));
    }

    public override void Configure(IFunctionsHostBuilder builder)
    {
        var configuration = builder.GetContext()
            .Configuration;

        builder.Services.Configure<Ngrok>(configuration.GetRequiredSection(nameof(Ngrok)));

        builder.Services.AddTransient<ComputerVisionClient>(_ =>
        {
            const string ComputerVisionKeyPath = "Azure:ComputerVision:Key";
            var computerVisionKey = configuration[ComputerVisionKeyPath];

            const string EndpointPath = "Azure:ComputerVision:Endpoint";
            var endpoint = configuration[EndpointPath];
            
            var credentials = new ApiKeyServiceClientCredentials(computerVisionKey);
            return new ComputerVisionClient(credentials){Endpoint = endpoint};
        });

        builder.Services.AddTransient<IDatabaseConnectionFactory, SqlServerConnectionFactory>(_ =>
        {
            const string DatabaseConnectionStringName = "Database";
            var connectionString = configuration.GetConnectionString(DatabaseConnectionStringName);

            return new SqlServerConnectionFactory(connectionString);
        });

        builder.Services.AddAutoMapper(profiles =>
        {
            profiles.AddProfile<TicketProfile>();
        });

    }
}