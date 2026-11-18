namespace StreamlinkVlcStudio.Core.Settings;

public sealed record LocalWebServerEndpoint(string InterfaceId, string Address, int PrefixLength, int Port);
