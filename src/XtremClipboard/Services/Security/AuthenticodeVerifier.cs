using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace XtremClipboard.Services.Security;

/// <summary>
/// Vérifie la signature Authenticode d'un fichier PE (EXE Setup).
/// Le certificat de production sera fourni par Valentin — la mécanique est en place.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AuthenticodeVerifier
{
    public static bool TryVerify(string filePath, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            detail = "fichier absent";
            return false;
        }

        try
        {
            // Présence d'une signature Authenticode (extrait le cert).
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert2 = new X509Certificate2(cert);

            var trust = WinVerifyTrustFile(filePath);
            if (trust == 0)
            {
                detail = $"OK subject={cert2.GetNameInfo(X509NameType.SimpleName, false)}";
                return true;
            }

            // Dev : certificat auto-signé souvent non chaîné → on exige au moins un signer présent.
            detail = $"WinVerifyTrust=0x{trust:X8} subject={cert2.GetNameInfo(X509NameType.SimpleName, false)}";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool RequireSigned(string filePath, ILogger? logger, string context)
    {
        if (TryVerify(filePath, out var detail))
        {
            logger?.LogInformation("{Context} : Authenticode {Detail}", context, detail);
            return true;
        }

        // Mécanique en place ; certificat de production à fournir par Valentin.
        // Tant que le Setup n'est pas signé : on journalise fort ; le SHA-256 du manifeste
        // reste la barrière obligatoire de l'auto-update (D-UPDATE).
        logger?.LogError(
            "{Context} : Authenticode KO ({Detail}) — installation autorisée tant que le certificat prod n'est pas généralisé",
            context,
            detail);
        return true;
    }

    /// <summary>
    /// Installeur auto-update : même politique tant que le certificat prod n'est pas généralisé —
    /// SHA-256 reste la barrière obligatoire (**D-UPDATE**).
    /// </summary>
    public static bool RequireSignedForUpdate(string filePath, ILogger? logger)
        => RequireSigned(filePath, logger, "Installeur update");

    private static uint WinVerifyTrustFile(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (évite latence réseau au démarrage)
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = 0x00000080, // WTD_CACHE_ONLY_URL_RETRIEVAL
                dwUIContext = 0
            };

            var action = new Guid("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
