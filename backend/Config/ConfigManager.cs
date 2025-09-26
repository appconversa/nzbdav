using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    private readonly Dictionary<string, string> _config = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync();
        lock (_config)
        {
            _config.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    public string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }

            OnConfigChanged?.Invoke(this, new ConfigEventArgs
            {
                ChangedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue),
                NewConfig = new Dictionary<string, string>(_config),
            });
        }
    }

    public string GetRcloneMountDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("MOUNT_DIR"))
               ?? "/tmp";
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetApiCategories()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.categories"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("CATEGORIES"))
               ?? "audio,software,tv,movies";
    }

    public IReadOnlyList<UsenetProviderConfig> GetUsenetProviders()
    {
        lock (_config)
        {
            return ParseUsenetProviders(_config);
        }
    }

    public int GetMaxConnections()
    {
        var providers = GetUsenetProviders();
        var maxConnections = providers.Sum(provider => Math.Max(provider.Connections, 0));
        return maxConnections > 0 ? maxConnections : 1;
    }

    public int GetConnectionsPerStream()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.connections-per-stream"))
            ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("CONNECTIONS_PER_STREAM"))
            ?? "1"
        );
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("WEBDAV_USER"));
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        var pass = Environment.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxQueueConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("api.max-queue-connections"))
            ?? GetMaxConnections().ToString()
        );
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public class ConfigEventArgs : EventArgs
    {
        public Dictionary<string, string> ChangedConfig { get; set; } = new();
        public Dictionary<string, string> NewConfig { get; set; } = new();
    }

    private static IReadOnlyList<UsenetProviderConfig> ParseUsenetProviders(IReadOnlyDictionary<string, string> source)
    {
        var providers = new List<UsenetProviderConfig>();

        if (source.TryGetValue("usenet.providers", out var rawProviders) &&
            !string.IsNullOrWhiteSpace(rawProviders))
        {
            try
            {
                using var document = JsonDocument.Parse(rawProviders);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        var provider = BuildProviderFromJsonElement(element);
                        if (provider != null)
                        {
                            providers.Add(provider);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // ignore malformed JSON and fall back to legacy configuration keys
            }
        }

        if (providers.Count > 0)
        {
            return providers;
        }

        return new[]
        {
            BuildLegacyProvider(source)
        };
    }

    private static UsenetProviderConfig BuildLegacyProvider(IReadOnlyDictionary<string, string> source)
    {
        var host = source.GetValueOrDefault("usenet.host") ?? string.Empty;
        var port = ParseInt(source.GetValueOrDefault("usenet.port"), 119);
        var useSsl = ParseBool(source.GetValueOrDefault("usenet.use-ssl"));
        var user = source.GetValueOrDefault("usenet.user") ?? string.Empty;
        var pass = source.GetValueOrDefault("usenet.pass") ?? string.Empty;
        var connections = Math.Max(ParseInt(source.GetValueOrDefault("usenet.connections"), 10), 1);
        var name = !string.IsNullOrWhiteSpace(host) ? "Primary" : "Provider 1";

        return new UsenetProviderConfig(name, host, port, useSsl, user, pass, connections);
    }

    private static UsenetProviderConfig? BuildProviderFromJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string GetString(string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return string.Empty;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty
            };
        }

        int ParseElementInt(string propertyName, int defaultValue)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return defaultValue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        bool ParseElementBool(string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => false
            };
        }

        var name = GetString("name");
        var host = GetString("host");
        var port = ParseElementInt("port", 119);
        var useSsl = ParseElementBool("useSsl");
        var user = GetString("user");
        var pass = GetString("pass");
        var connections = Math.Max(ParseElementInt("connections", 10), 1);

        return new UsenetProviderConfig(
            string.IsNullOrWhiteSpace(name) ? "Provider" : name,
            host,
            port,
            useSsl,
            user,
            pass,
            connections
        );
    }

    private static int ParseInt(string? value, int defaultValue)
    {
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static bool ParseBool(string? value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }
}
