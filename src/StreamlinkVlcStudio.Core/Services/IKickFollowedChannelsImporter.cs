namespace StreamlinkVlcStudio.Core.Services;

public interface IKickFollowedChannelsImporter
{
    // Null means the user closed the sign-in/import window without importing.
    // Opening the sign-in window explicitly lets the user switch accounts or clear the saved session.
    Task<IReadOnlyList<string>?> ImportAsync(CancellationToken cancellationToken = default, bool openSignInWindow = false);
}
