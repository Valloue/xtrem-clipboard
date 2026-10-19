namespace XtremClipboard.Services;

/// <summary>
/// Gestionnaires de mots de passe et coffres-forts toujours exclus (D-CLIP-PRIVACY).
/// Comparaison sur le nom de fichier, sans espaces ni extension. L'utilisateur ne choisit pas cette liste.
/// </summary>
internal static class BuiltInExclusions
{
    /// <summary>Nom normalisé entier (processus courts, sans préfixe large).</summary>
    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        "bdpwm",
        "ewallet",
        "keeper",
        "kpm",
        "pwsafe",
        "stpass"
    };

    /// <summary>Le nom normalisé commence par l'un de ces préfixes.</summary>
    private static readonly string[] Prefixes =
    [
        "1password",
        "authpass",
        "authy",
        "avirapassword",
        "avastpasswords",
        "avgpasswords",
        "bitdefenderwallet",
        "bitwarden",
        "buttercup",
        "cyberark",
        "dashlane",
        "delinea",
        "dropboxpasswords",
        "duodesktop",
        "encryptr",
        "enpass",
        "fsecurekey",
        "gopass",
        "icloudpasswords",
        "kasperskypassword",
        "keepass",
        "keeperpassword",
        "keepersecurity",
        "keeweb",
        "lastpass",
        "logmeonce",
        "microsoftauthenticator",
        "msecure",
        "myki",
        "nordpass",
        "nortonpassword",
        "oktaverify",
        "onlykey",
        "padloc",
        "passky",
        "passwordboss",
        "passworddepot",
        "passwordgorilla",
        "passwordsafe",
        "passwork",
        "pleasantpassword",
        "privateark",
        "protonpass",
        "psono",
        "qtpass",
        "remembear",
        "roboform",
        "safeincloud",
        "secretserver",
        "splashid",
        "steganos",
        "stickypassword",
        "thycotic",
        "trendmicropassword",
        "truekey",
        "winauth",
        "yubicoauthenticator",
        "zohovault",
        "2fast"
    ];

    public static bool Matches(string? processFileName)
    {
        var key = Normalize(processFileName);
        if (key.Length == 0)
            return false;
        if (Exact.Contains(key))
            return true;

        foreach (var prefix in Prefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Lettres et chiffres, sans extension ni espaces. Ex. « Proton Pass.exe » → protonpass.</summary>
    private static string Normalize(string? processFileName)
    {
        if (string.IsNullOrWhiteSpace(processFileName))
            return string.Empty;

        var name = Path.GetFileName(processFileName.Trim().Trim('"'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        Span<char> buffer = stackalloc char[name.Length];
        var n = 0;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                buffer[n++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..n]);
    }
}
