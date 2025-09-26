using System.Collections.Immutable;

namespace NzbWebDAV.Database.Models;

public class ConfigItem
{
    public static readonly ImmutableHashSet<string> Keys = ImmutableHashSet.Create([
        "api.key",
        "api.categories",
        "usenet.host",
        "usenet.port",
        "usenet.use-ssl",
        "usenet.connections",
        "usenet.providers",
        "usenet.user",
        "usenet.pass",
        "webdav.user",
        "webdav.pass",
        "rclone.mount-dir",
    ]);

    public string ConfigName { get; set; } = null!;
    public string ConfigValue { get; set; } = null!;
}