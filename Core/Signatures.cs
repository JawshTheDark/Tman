using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Tman.Core;

public enum SignatureState { Valid, Unsigned, Untrusted, Error }

public sealed record SignatureInfo(SignatureState State, string Signer, bool ViaCatalog)
{
    public string Text => State switch
    {
        SignatureState.Valid => Signer.Length > 0 ? Signer : "Signed",
        SignatureState.Unsigned => "Not signed",
        SignatureState.Untrusted => Signer.Length > 0 ? $"Not trusted ({Signer})" : "Not trusted",
        _ => "Couldn't check",
    };
}

/// <summary>
/// Authenticode checks like Process Explorer's "Verified Signer": the file's own signature, or failing that the
/// Windows security catalogs (how most Windows binaries are signed). Results are cached by path, and queued
/// checks run one at a time on a low-priority background thread because verifying hashes the whole file.
/// </summary>
internal static class Signatures
{
    static readonly ConcurrentDictionary<string, SignatureInfo> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly BlockingCollection<string> Queue = new();

    /// <summary>Raised on the background thread when a queued check finishes.</summary>
    public static event Action<string, SignatureInfo>? Verified;

    static Signatures()
    {
        new Thread(Worker) { IsBackground = true, Name = "Tman signatures", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>The cached result, or null after queuing a check (watch <see cref="Verified"/>).</summary>
    public static SignatureInfo? GetOrQueue(string path)
    {
        if (Cache.TryGetValue(path, out var s)) return s;
        if (Pending.TryAdd(path, 0)) Queue.Add(path);
        return null;
    }

    static void Worker()
    {
        foreach (var path in Queue.GetConsumingEnumerable())
        {
            var result = Check(path);
            Cache[path] = result;
            Pending.TryRemove(path, out _);
            try { Verified?.Invoke(path, result); } catch { }
        }
    }

    /// <summary>Verifies synchronously (and caches). Can take a while for big files.</summary>
    public static SignatureInfo Check(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        SignatureInfo result;
        try
        {
            if (!File.Exists(path)) return new SignatureInfo(SignatureState.Error, "", false);
            var (code, signer) = VerifyEmbedded(path);
            if (code == 0) result = new SignatureInfo(SignatureState.Valid, signer, false);
            else if (code is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
            {
                var cat = VerifyCatalog(path);
                result = cat is { } c
                    ? new SignatureInfo(c.Code == 0 ? SignatureState.Valid : SignatureState.Untrusted, c.Signer, true)
                    : new SignatureInfo(SignatureState.Unsigned, "", false);
            }
            else result = new SignatureInfo(SignatureState.Untrusted, signer, false);
        }
        catch
        {
            result = new SignatureInfo(SignatureState.Error, "", false);
        }
        Cache[path] = result;
        return result;
    }

    // ---- WinVerifyTrust ----

    const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_CHOICE_CATALOG = 2;
    const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
    const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;
    static readonly IntPtr NoWindow = new(-1);
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll")]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll")]
    static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll")]
    static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIndex, bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll")]
    static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem, string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll")]
    static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, IntPtr file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll")]
    static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, uint hashSize, uint flags, IntPtr previous);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CATALOG_INFO info, uint flags);

    [DllImport("wintrust.dll")]
    static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    [DllImport("wintrust.dll")]
    static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
    static extern uint CertGetNameStringW(IntPtr cert, uint type, uint flags, IntPtr typePara, StringBuilder name, uint size);

    static (int Code, string Signer) VerifyEmbedded(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pathPtr }, info, false);
            return Run(WTD_CHOICE_FILE, info);
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    /// <summary>Looks the file's hash up in the system catalogs (SHA-256 first, then SHA-1) and verifies that catalog.</summary>
    static (int Code, string Signer)? VerifyCatalog(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var file = fs.SafeFileHandle.DangerousGetHandle();
        foreach (var algorithm in new[] { "SHA256", null })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            try
            {
                uint size = 0;
                fs.Position = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
                if (size == 0 || size > 64) continue;
                var hash = new byte[size];
                fs.Position = 0;
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0)) continue;
                var catInfo = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                if (catInfo == IntPtr.Zero) continue;
                try
                {
                    var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>(), wszCatalogFile = "" };
                    if (!CryptCATCatalogInfoFromContext(catInfo, ref ci, 0)) continue;
                    return VerifyCatalogMember(ci.wszCatalogFile, Convert.ToHexString(hash), path, file, hash, admin);
                }
                finally
                {
                    CryptCATAdminReleaseCatalogContext(admin, catInfo, 0);
                }
            }
            finally
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }
        return null;
    }

    static (int Code, string Signer) VerifyCatalogMember(string catalog, string tag, string path, IntPtr file, byte[] hash, IntPtr admin)
    {
        var catalogPtr = Marshal.StringToHGlobalUni(catalog);
        var tagPtr = Marshal.StringToHGlobalUni(tag);
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var hashPtr = Marshal.AllocHGlobal(hash.Length);
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
        try
        {
            Marshal.Copy(hash, 0, hashPtr, hash.Length);
            Marshal.StructureToPtr(new WINTRUST_CATALOG_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                pcwszCatalogFilePath = catalogPtr,
                pcwszMemberTag = tagPtr,
                pcwszMemberFilePath = pathPtr,
                hMemberFile = file,
                pbCalculatedFileHash = hashPtr,
                cbCalculatedFileHash = (uint)hash.Length,
                hCatAdmin = admin,
            }, info, false);
            return Run(WTD_CHOICE_CATALOG, info);
        }
        finally
        {
            foreach (var p in new[] { info, hashPtr, pathPtr, tagPtr, catalogPtr }) Marshal.FreeHGlobal(p);
        }
    }

    static (int Code, string Signer) Run(uint choice, IntPtr union)
    {
        var action = GenericVerifyV2;
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice,
            pUnion = union,
            dwStateAction = WTD_STATEACTION_VERIFY,
            // Offline only: no revocation lookups, so a check never waits on the network.
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        int code = WinVerifyTrust(NoWindow, ref action, ref data);
        string signer = "";
        try { signer = SignerName(data.hWVTStateData); } catch { }
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(NoWindow, ref action, ref data);
        return (code, signer);
    }

    static string SignerName(IntPtr state)
    {
        if (state == IntPtr.Zero) return "";
        var prov = WTHelperProvDataFromStateData(state);
        if (prov == IntPtr.Zero) return "";
        var signer = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (signer == IntPtr.Zero) return "";
        var provCert = WTHelperGetProvCertFromChain(signer, 0);
        if (provCert == IntPtr.Zero) return "";
        var cert = Marshal.ReadIntPtr(provCert, IntPtr.Size); // CRYPT_PROVIDER_CERT.pCert follows cbStruct (padded)
        if (cert == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        CertGetNameStringW(cert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, IntPtr.Zero, sb, (uint)sb.Capacity);
        return sb.ToString();
    }
}
