using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;

namespace LeadSphere.Discovery.Function.Infrastructure;

public interface ISqlConnectionFactory
{
    SqlConnection CreateConnection();
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("LeadSphereDb")
            ?? throw new InvalidOperationException("Connection string 'LeadSphereDb' is not configured.");
    }

    public SqlConnection CreateConnection() => new(_connectionString);
}
