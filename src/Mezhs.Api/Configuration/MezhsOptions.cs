namespace Mezhs.Configuration;

public sealed class MezhsOptions
{
    public int Version { get; set; } = 1;
    public ServerOptions Server { get; set; } = new();
    public TransportOptions Transport { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public List<ConnectionOptions> Connections { get; set; } = [];
}

public sealed class ServerOptions
{
    public string Listen { get; set; } = "http://127.0.0.1:5050";
}

public sealed class TransportOptions
{
    public string Type { get; set; } = "electron";
    public int IdleMinutes { get; set; } = 15;
    public string ElectronDirectory { get; set; } = "electron";
}

public sealed class StorageOptions
{
    public string Root { get; set; } = "data";
}

public sealed class ConnectionOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Integration { get; set; } = "";
    public string? Project { get; set; }
}
