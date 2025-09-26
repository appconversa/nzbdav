namespace NzbWebDAV.Config;

public record UsenetProviderConfig(
    string Name,
    string Host,
    int Port,
    bool UseSsl,
    string User,
    string Pass,
    int Connections
);
