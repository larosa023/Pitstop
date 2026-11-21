using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Pitstop.Native;

/// <summary>Authenticode checks: is this file signed, is the signature intact, and who signed it.</summary>
internal static unsafe class WinTrust
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public IntPtr hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, Guid* pgActionID, WINTRUST_DATA* pWVTData);

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    /// <summary>
    /// Returns null when <paramref name="path"/> has a valid Authenticode signature from
    /// <paramref name="expectedOrg"/>; otherwise a plain-English reason.
    /// </summary>
    public static string? CheckSignedBy(string path, string expectedOrg)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
        int result;
        fixed (char* p = path)
        {
            var file = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = p };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = &file,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            result = WinVerifyTrust(new IntPtr(-1), &action, &data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), &action, &data);
        }

        if (result != 0)
        {
            return unchecked((uint)result) switch
            {
                0x800B0100 => "the file isn't signed",                 // TRUST_E_NOSIGNATURE
                0x80096010 => "the file was changed after it was signed",   // TRUST_E_BAD_DIGEST
                0x800B0101 => "its signing certificate has expired",   // CERT_E_EXPIRED (when not timestamped)
                0x800B0109 => "its signing certificate isn't trusted", // CERT_E_UNTRUSTEDROOT
                0x800B0111 => "its signer is blocked on this PC",      // TRUST_E_EXPLICIT_DISTRUST
                _ => $"Windows rejected its signature (0x{result:X8})",
            };
        }

        try
        {
            var cert = X509Certificate.CreateFromSignedFile(path);
            var subject = cert.Subject;
            if (!subject.Contains($"O={expectedOrg}", StringComparison.OrdinalIgnoreCase) &&
                !subject.Contains($"O=\"{expectedOrg}\"", StringComparison.OrdinalIgnoreCase))
                return $"it's signed by someone else ({subject})";
        }
        catch (Exception ex)
        {
            return "its signer couldn't be read (" + ex.Message + ")";
        }
        return null;
    }
}
