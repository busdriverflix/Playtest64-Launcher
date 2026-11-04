namespace Playtest64.Launcher;

/// <summary>One build, for this system: the file of it that matches the platform.</summary>
public sealed record BuildInfo(int BuildId, int FileId, string Version, string Notes, string FileName, long Size)
{
    public override string ToString() => Version;
}

/// <summary>Something the person should be told in words: no network, a wrong password, a damaged download.</summary>
public class LauncherException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server no longer accepts the token (it ran out): the person has to sign in again.</summary>
public sealed class SignedOutException() : LauncherException("Die Anmeldung ist abgelaufen. Bitte melde dich neu an.");
