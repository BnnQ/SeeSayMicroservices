using System.Data;
using Microsoft.Data.SqlClient;
using SeeSayMicroservices.Services.Abstractions;

namespace SeeSayMicroservices.Services;

public class SqlServerConnectionFactory : IDatabaseConnectionFactory
{
    private readonly string connectionString;

    public SqlServerConnectionFactory(string connectionString)
    {
        this.connectionString = connectionString;
    }
    
    public IDbConnection CreateConnection()
    {
        return new SqlConnection(connectionString);
    }
}