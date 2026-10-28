using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Core.Services;

namespace NetworkProfileManager.Infrastructure.SessionProviders;
public sealed class StandardSessionProvider : IProxySessionProvider
{
    public bool SupportsSticky => false;
    public ProxyConfiguration Resolve(ProxyConfiguration gateway, string profileId)
    {
        gateway.Validate();
        if (gateway.RotationMode == RotationMode.Sticky)
            throw new NotSupportedException("Sticky Session chưa được cấu hình");
        return gateway;
    }
}
