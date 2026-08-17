namespace WebSSMS.Models;

public class ConnectionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ServerName { get; set; } = string.Empty;
    public string? DatabaseName { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Port { get; set; } = 1433;
    public bool TrustServerCertificate { get; set; } = true;
    public bool Encrypt { get; set; } = true;
    public int ConnectTimeout { get; set; } = 15;
    public string? ApplicationName { get; set; } = "WebSSMS";
    public string? DisplayName { get; set; }

    public string ConnectionString =>
        $"Server={ServerName},{Port};Database={DatabaseName ?? "master"};User Id={UserName};Password={Password};" +
        $"TrustServerCertificate={TrustServerCertificate};Encrypt={Encrypt};Connection Timeout={ConnectTimeout};" +
        $"Application Name={ApplicationName};MultipleActiveResultSets=True";

    public string DisplayLabel => DisplayName ?? $"{ServerName} ({UserName})";
}

public class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = "Local Servers";
    public ConnectionInfo ConnectionInfo { get; set; } = new();
}
