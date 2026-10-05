namespace ClaudeTracker.Core;

/// <summary>What the app does about its sign-in entry when it starts.</summary>
/// <param name="Preference">The preference to save, or null to leave it as it is.</param>
/// <param name="Register">Whether to write the entry, for the copy that is running.</param>
public readonly record struct LoginItemStep(bool? Preference, bool Register);

/// <summary>
/// The pure half of "Launch when I sign in to Windows": what Windows' own records mean and
/// what the app does about them. The Mac app asks its system for this (<c>SMAppService</c>);
/// on Windows it is two registry values, read and written by the app project.
/// </summary>
public static class LoginItem
{
    /// <summary>The entry's name: under Windows' Run key, and in its record of what the user switched off.</summary>
    public const string EntryName = "ClaudeTracker";

    /// <summary>What the entry runs: this copy, started without showing anything.</summary>
    public static string Command(string executablePath) => $"\"{executablePath}\" --background";

    /// <summary>The executable a command names, or null when it names none.</summary>
    public static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    /// <summary>
    /// Whether Windows will run the entry. Task Manager and Settings do not remove an entry
    /// the user switches off: they note it beside it, in a value whose first byte is even
    /// while the entry is allowed and odd once it is switched off. Seen written by Windows 11's
    /// Settings on 2026-10-05: 01 and the time for off, twelve zeros for on again; Task Manager
    /// is documented to write 03 and 02, and 07 and 06 also occur. No note means nobody ever
    /// switched it off.
    /// </summary>
    public static bool IsAllowed(ReadOnlySpan<byte> note) => note.IsEmpty || (note[0] & 1) == 0;

    /// <summary>
    /// What to do about the entry at start. The app opts in once, on the first run of an
    /// installed copy, and from then on follows what was done elsewhere instead of undoing it.
    /// </summary>
    /// <param name="installed">This copy was put there by the setup. One run from a build folder never enters the user's sign-in.</param>
    /// <param name="preference">The saved preference; null when the app has never decided.</param>
    /// <param name="registered">The entry's command as Windows has it, or null without an entry.</param>
    /// <param name="allowed"><see cref="IsAllowed"/> of Windows' note for the entry.</param>
    /// <param name="wanted"><see cref="Command"/> for the copy that is running.</param>
    /// <param name="registeredCopyExists">Whether the executable the entry names is still on disk.</param>
    /// <param name="justInstalled">
    /// The setup has only just put this copy here, and not over an earlier one. An entry that is
    /// missing then was taken away by the uninstaller with the copy before, not by the user.
    /// </param>
    public static LoginItemStep AtStart(bool installed, bool? preference, string? registered, bool allowed, string wanted,
                                        bool registeredCopyExists, bool justInstalled = false)
    {
        if (!installed) return default;
        // The first run: opt in — unless an earlier install's entry was switched off, which still stands.
        if (preference is null) return allowed ? new(true, true) : new(false, false);
        var same = string.Equals(ExecutablePath(registered), ExecutablePath(wanted), StringComparison.OrdinalIgnoreCase);
        // Off in the app, yet this copy's entry is there and allowed: it was switched back on
        // in Windows' own list. Windows will start the app, and the switch says what is true.
        if (preference == false) return registered is not null && allowed && same ? new(true, false) : default;
        // Installed again after an uninstall, which took the entry with it: on it was, on it stays.
        if (registered is null && allowed && justInstalled) return new(null, true);
        // Removed or switched off somewhere else: the switch follows, and the app leaves it so.
        if (registered is null || !allowed) return new(false, false);
        // The entry names a copy that is gone: the app was installed again somewhere else.
        return !same && !registeredCopyExists ? new(null, true) : default;
    }
}
