using System.Runtime.InteropServices;
using System.Text;

namespace Pitstop.Native;

public sealed class NvApiException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The parts of NVIDIA's NvAPI that Pitstop uses, called through nvapi64.dll's single export
/// (nvapi_QueryInterface) with the function ids from NVIDIA's public nvapi_interface.h.
/// Structs are laid out by hand from nvapi.h (pack 4) so nothing depends on marshalling.
/// </summary>
public static unsafe class NvApi
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    public const int Ok = 0;
    public const int EndEnumeration = -7;
    public const int InvalidUserPrivilege = -137;
    public const int SettingNotFound = -160;
    public const int ProfileNotFound = -163;
    public const int ProfileNameInUse = -164;
    public const int ExecutableNotFound = -166;
    public const int ExecutableAlreadyInUse = -167;

    // ---- struct layouts (nvapi.h, #pragma pack(4)) ----
    public const int UnicodeChars = 2048;              // NvAPI_UnicodeString = NvU16[2048]
    public const int UnicodeBytes = UnicodeChars * 2;

    // NVDRS_SETTING_V1
    public const int SettingSize = 4 + UnicodeBytes + 4 * 5 + 4100 + 4100;   // 12320
    public const uint SettingVer = SettingSize | (1u << 16);
    public const int SetName = 4, SetId = 4100, SetType = 4104, SetLocation = 4108,
        SetIsCurrentPredefined = 4112, SetIsPredefinedValid = 4116, SetPredefined = 4120, SetCurrent = 8220;

    // NVDRS_PROFILE_V1
    public const int ProfileSize = 4 + UnicodeBytes + 4 * 4;                  // 4116
    public const uint ProfileVer = ProfileSize | (1u << 16);
    public const int ProfName = 4, ProfGpuSupport = 4100, ProfIsPredefined = 4104, ProfNumApps = 4108, ProfNumSettings = 4112;

    // NVDRS_APPLICATION_V3
    public const int AppSize = 4 + 4 + UnicodeBytes * 4 + 4;                  // 16396
    public const uint AppVer = AppSize | (3u << 16);
    public const int AppIsPredefined = 4, AppName = 8, AppFriendly = 8 + UnicodeBytes, AppLauncher = 8 + UnicodeBytes * 2,
        AppFileInFolder = 8 + UnicodeBytes * 3, AppFlags = 8 + UnicodeBytes * 4;

    private static readonly object Gate = new();
    private static bool _tried, _ok;

    /// <summary>Why NvAPI isn't usable (no NVIDIA driver, etc.), or null.</summary>
    public static string? InitError { get; private set; }

    /// <summary>All NvAPI calls go through this lock: the library isn't documented as thread-safe.</summary>
    public static object Lock => Gate;

    public static bool Initialize()
    {
        lock (Gate)
        {
            if (_tried) return _ok;
            _tried = true;
            try
            {
                if (!Environment.Is64BitProcess)
                {
                    InitError = "Pitstop needs 64-bit Windows.";
                    return false;
                }
                var init = (delegate* unmanaged[Cdecl]<int>)Fn(0x0150E828);
                int r = init();
                _ok = r == Ok;
                if (!_ok) InitError = r == -6 ? "No NVIDIA graphics card is driving a display." : $"NVIDIA's driver API didn't start ({Describe(r)}).";
            }
            catch (DllNotFoundException)
            {
                InitError = "No NVIDIA driver is installed.";
            }
            catch (EntryPointNotFoundException)
            {
                InitError = "The NVIDIA driver API is missing from this driver.";
            }
            catch (Exception ex)
            {
                InitError = ex.Message;
            }
            return _ok;
        }
    }

    private static void* Fn(uint id)
    {
        var p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new NvApiException($"This driver doesn't support NvAPI function 0x{id:X8}.", -3);
        return (void*)p;
    }

    public static void Check(int status, string what)
    {
        if (status != Ok) throw new NvApiException($"{what} failed: {Describe(status)}", status);
    }

    public static string Describe(int status)
    {
        try
        {
            var f = (delegate* unmanaged[Cdecl]<int, byte*, int>)Fn(0x6C2D048C);
            byte* buf = stackalloc byte[64];
            new Span<byte>(buf, 64).Clear();
            if (f(status, buf) == Ok)
            {
                var s = ReadAnsi(buf, 64);
                if (s.Length > 0) return $"{s} ({status})";
            }
        }
        catch
        {
            // fall through
        }
        return status switch
        {
            InvalidUserPrivilege => "needs admin rights",
            SettingNotFound => "setting not found",
            ProfileNotFound => "profile not found",
            _ => $"NvAPI error {status}",
        };
    }

    // ---------------------------------------------------------------- system / GPU

    public static (uint version, string branch) GetDriverAndBranch()
    {
        var f = (delegate* unmanaged[Cdecl]<uint*, byte*, int>)Fn(0x2926AAAD);
        uint v = 0;
        byte* buf = stackalloc byte[64];
        new Span<byte>(buf, 64).Clear();
        Check(f(&v, buf), "Reading the driver version");
        return (v, ReadAnsi(buf, 64));
    }

    public static List<IntPtr> EnumPhysicalGpus()
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr*, uint*, int>)Fn(0xE5AC921F);
        IntPtr* handles = stackalloc IntPtr[64];
        uint count = 0;
        Check(f(handles, &count), "Listing GPUs");
        var list = new List<IntPtr>();
        for (int i = 0; i < count && i < 64; i++) list.Add(handles[i]);
        return list;
    }

    public static string GetFullName(IntPtr gpu)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Fn(0xCEEE8E9F);
        byte* buf = stackalloc byte[64];
        new Span<byte>(buf, 64).Clear();
        Check(f(gpu, buf), "Reading the GPU name");
        return ReadAnsi(buf, 64);
    }

    /// <summary>Video memory in bytes, or 0.</summary>
    public static long GetFrameBufferBytes(IntPtr gpu)
    {
        try
        {
            var f = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Fn(0x46FBEB03);
            uint kb = 0;
            return f(gpu, &kb) == Ok ? kb * 1024L : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------- DRS (driver settings)

    public static IntPtr DrsCreateSession()
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr*, int>)Fn(0x0694D52E);
        IntPtr h;
        Check(f(&h), "Opening the driver settings");
        return h;
    }

    public static void DrsDestroySession(IntPtr s)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(0xDAD9CFF8);
        f(s);
    }

    public static void DrsLoadSettings(IntPtr s)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(0x375DBD6B);
        Check(f(s), "Loading the driver settings");
    }

    public static void DrsSaveSettings(IntPtr s)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(0xFCBC7E14);
        Check(f(s), "Saving the driver settings");
    }

    public static IntPtr DrsGetCurrentGlobalProfile(IntPtr s)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int>)Fn(0x617BFF9F);
        IntPtr h;
        Check(f(s, &h), "Finding the global profile");
        return h;
    }

    public static uint DrsGetNumProfiles(IntPtr s)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Fn(0x1DAE4FBC);
        uint n;
        Check(f(s, &n), "Counting profiles");
        return n;
    }

    /// <summary>Returns false at the end of the list.</summary>
    public static bool DrsEnumProfiles(IntPtr s, uint index, out IntPtr profile)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr*, int>)Fn(0xBC371EE0);
        IntPtr h;
        int r = f(s, index, &h);
        profile = h;
        if (r == EndEnumeration) return false;
        Check(r, "Listing profiles");
        return true;
    }

    public static (string name, bool predefined, uint apps, uint settings) DrsGetProfileInfo(IntPtr s, IntPtr p)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)Fn(0x61CD6FD6);
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)ProfileSize);
        try
        {
            *(uint*)buf = ProfileVer;
            Check(f(s, p, buf), "Reading a profile");
            return (ReadUnicode(buf + ProfName), *(uint*)(buf + ProfIsPredefined) != 0,
                *(uint*)(buf + ProfNumApps), *(uint*)(buf + ProfNumSettings));
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    public static IntPtr? DrsFindProfileByName(IntPtr s, string name)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)Fn(0x7E4A9A0B);
        byte* str = (byte*)NativeMemory.AllocZeroed((nuint)UnicodeBytes);
        try
        {
            WriteUnicode(str, name);
            IntPtr h;
            int r = f(s, str, &h);
            if (r == ProfileNotFound) return null;
            Check(r, "Finding a profile");
            return h;
        }
        finally
        {
            NativeMemory.Free(str);
        }
    }

    public static IntPtr DrsCreateProfile(IntPtr s, string name)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)Fn(0xCC176068);
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)ProfileSize);
        try
        {
            *(uint*)buf = ProfileVer;
            WriteUnicode(buf + ProfName, name);
            IntPtr h;
            Check(f(s, buf, &h), "Creating a profile");
            return h;
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    public static List<string> DrsEnumApplications(IntPtr s, IntPtr p, uint count)
    {
        var list = new List<string>();
        if (count == 0) return list;
        var f = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint*, byte*, int>)Fn(0x7FA2173A);
        uint batch = Math.Min(count, 64u);
        nuint bytes = (nuint)(AppSize * (int)batch);
        byte* buf = (byte*)NativeMemory.AllocZeroed(bytes);
        try
        {
            uint start = 0;
            while (start < count)
            {
                new Span<byte>(buf, (int)bytes).Clear();
                for (int i = 0; i < batch; i++) *(uint*)(buf + i * AppSize) = AppVer;
                uint n = batch;
                int r = f(s, p, start, &n, buf);
                if (r == EndEnumeration || n == 0) break;
                Check(r, "Listing a profile's programs");
                for (int i = 0; i < n; i++)
                {
                    var name = ReadUnicode(buf + i * AppSize + AppName);
                    if (name.Length > 0) list.Add(name);
                }
                start += n;
            }
        }
        finally
        {
            NativeMemory.Free(buf);
        }
        return list;
    }

    public static void DrsCreateApplication(IntPtr s, IntPtr p, string exe)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)Fn(0x4347A9DE);
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)AppSize);
        try
        {
            *(uint*)buf = AppVer;
            WriteUnicode(buf + AppName, exe);
            WriteUnicode(buf + AppFriendly, exe);
            int r = f(s, p, buf);
            if (r == ExecutableAlreadyInUse) return;   // another profile already claims it: leave that alone
            Check(r, $"Adding {exe} to a profile");
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    public struct SettingRead
    {
        public bool Found;
        public uint Value;
        /// <summary>0 = this profile, 1 = global, 2 = base, 3 = driver default.</summary>
        public int Location;
        public bool IsPredefined;
        public bool PredefinedValid;
        public uint PredefinedValue;
        public int Type;
    }

    public static SettingRead DrsGetSetting(IntPtr s, IntPtr p, uint id)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, byte*, int>)Fn(0x73BF8338);
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)SettingSize);
        try
        {
            *(uint*)buf = SettingVer;
            int r = f(s, p, id, buf);
            if (r == SettingNotFound) return default;
            Check(r, $"Reading setting 0x{id:X8}");
            return new SettingRead
            {
                Found = true,
                Type = *(int*)(buf + SetType),
                Location = *(int*)(buf + SetLocation),
                IsPredefined = *(uint*)(buf + SetIsCurrentPredefined) != 0,
                PredefinedValid = *(uint*)(buf + SetIsPredefinedValid) != 0,
                PredefinedValue = *(uint*)(buf + SetPredefined),
                Value = *(uint*)(buf + SetCurrent),
            };
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    public static int DrsSetDword(IntPtr s, IntPtr p, uint id, uint value)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)Fn(0x577DD202);
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)SettingSize);
        try
        {
            *(uint*)buf = SettingVer;
            *(uint*)(buf + SetId) = id;
            *(int*)(buf + SetType) = 0;   // NVDRS_DWORD_TYPE
            *(uint*)(buf + SetCurrent) = value;
            return f(s, p, buf);
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    /// <summary>Back to NVIDIA's value for this profile (or no value at all). Missing settings are fine.</summary>
    public static int DrsRestoreProfileDefaultSetting(IntPtr s, IntPtr p, uint id)
    {
        var f = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)Fn(0x53F0381E);
        int r = f(s, p, id);
        if (r == SettingNotFound) return Ok;
        if (r != Ok)
        {
            // Older drivers: delete the user value instead.
            var del = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)Fn(0xE4A26362);
            int r2 = del(s, p, id);
            if (r2 == Ok || r2 == SettingNotFound) return Ok;
        }
        return r;
    }

    // ---------------------------------------------------------------- strings

    private static string ReadAnsi(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.ASCII.GetString(p, n).Trim();
    }

    private static string ReadUnicode(byte* p)
    {
        var c = (char*)p;
        int n = 0;
        while (n < UnicodeChars && c[n] != '\0') n++;
        return new string(c, 0, n);
    }

    private static void WriteUnicode(byte* p, string s)
    {
        var c = (char*)p;
        int n = Math.Min(s.Length, UnicodeChars - 1);
        for (int i = 0; i < n; i++) c[i] = s[i];
        c[n] = '\0';
    }
}
