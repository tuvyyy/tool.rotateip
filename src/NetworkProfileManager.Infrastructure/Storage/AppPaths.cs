using System.Security.Cryptography;
using System.Text;

namespace NetworkProfileManager.Infrastructure.Storage;

public sealed class AppPaths
{
    public string Root { get; }
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkProfileManager");
    public string MutexName => Root.Equals(Path.GetFullPath(DefaultRoot), StringComparison.OrdinalIgnoreCase)
        ? "NetworkProfileManager-V1"
        : "NetworkProfileManager-V1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..16];
    public string Database => Path.Combine(Root, "manager.db");
    public string Profiles => Path.Combine(Root, "Profiles");
    public AppPaths(string? root = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? DefaultRoot));
        Directory.CreateDirectory(Root);
    }
}
