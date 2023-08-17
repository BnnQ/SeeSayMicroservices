using System.Data;

namespace SeeSayMicroservices.Services.Abstractions;

public interface IDatabaseConnectionFactory
{
    public IDbConnection CreateConnection();
}