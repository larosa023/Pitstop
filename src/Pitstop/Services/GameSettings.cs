using Pitstop.Native;

namespace Pitstop.Services;

public sealed record SettingWrite(uint Id, uint Value, bool Optional = false);

public sealed class SettingOption
{
    public required string Label { get; init; }
    public SettingWrite[] Writes { get; init; } = Array.Empty<SettingWrite>();
}

public sealed class SettingDef
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Help { get; init; }
    public required uint PrimaryId { get; init; }
    public uint[] Ids { get; init; } = Array.Empty<uint>();
    public required List<SettingOption> Options { get; init; }
    public bool GlobalOnly { get; init; }
    public bool GameOnly { get; init; }
    /// <summary>Frame rate: lets you type any number.</summary>
    public bool AllowCustom { get; init; }
    public uint CustomMin { get; init; }
    public uint CustomMax { get; init; }

    public IEnumerable<uint> AllIds => Ids.Length > 0 ? Ids : new[] { PrimaryId };

    /// <summary>The option the current values match, or null (driver default / something else).</summary>
    public SettingOption? Match(IReadOnlyDictionary<uint, NvApi.SettingRead> values)
    {
        if (!values.TryGetValue(PrimaryId, out var primary) || !primary.Found) return null;
        foreach (var o in Options)
        {
            bool ok = true;
            foreach (var w in o.Writes)
            {
                if (values.TryGetValue(w.Id, out var v) && v.Found)
                {
                    if (v.Value != w.Value) { ok = false; break; }
                }
                else if (!w.Optional)
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return o;
        }
        return null;
    }
}

/// <summary>The handful of NVIDIA Control Panel settings people actually change, with ids from NVIDIA's NvApiDriverSettings.h.</summary>
public static class GameSettings
{
    public const uint FrameRateLimit = 0x10835002;    // FRL_FPS_ID
    public const uint PreRenderLimit = 0x007BA09E;    // PRERENDERLIMIT_ID ("Low Latency Mode" On = 1)
    public const uint UllCplState = 0x0005F543;       // Ultra Low Latency CPL state (0 off, 1 on, 2 ultra)
    public const uint UllEnabled = 0x10835000;        // Ultra Low Latency enabled
    public const uint VSync = 0x00A879CF;             // VSYNCMODE_ID
    public const uint PowerMode = 0x1057EB71;         // PREFERRED_PSTATE_ID
    public const uint TextureQuality = 0x00CE2691;    // QUALITY_ENHANCEMENTS_ID
    public const uint ShaderCacheSize = 0x00AC8497;   // PS_SHADERDISKCACHE_MAX_SIZE_ID (MB)
    public const uint GsyncApp = 0x10A879CF;          // VRR_APP_OVERRIDE_ID
    public const uint GsyncAppRequest = 0x10A879AC;   // VRR_APP_OVERRIDE_REQUEST_STATE_ID
    public const uint GsyncMode = 0x1194F158;         // VRR_MODE_ID (global)

    private static SettingOption O(string label, params SettingWrite[] w) => new() { Label = label, Writes = w };
    private static SettingOption V(string label, uint id, uint value) => O(label, new SettingWrite(id, value));

    public static readonly List<SettingDef> All = new()
    {
        new()
        {
            Key = "fps", Label = "Max frame rate", PrimaryId = FrameRateLimit,
            Help = "Caps how many frames per second the game renders. A cap a few below your refresh rate pairs well with G-SYNC.",
            AllowCustom = true, CustomMin = 20, CustomMax = 1000,
            Options = new uint[] { 0, 30, 45, 60, 72, 90, 100, 117, 120, 138, 141, 144, 157, 162, 165, 171, 200, 225, 237, 240, 280, 300, 360 }
                .Select(v => V(v == 0 ? "Off" : $"{v} FPS", FrameRateLimit, v)).ToList(),
        },
        new()
        {
            Key = "latency", Label = "Low latency mode", PrimaryId = PreRenderLimit, Ids = new[] { PreRenderLimit, UllCplState, UllEnabled },
            Help = "On lets the CPU queue only one frame. Ultra sends frames just in time. Games with NVIDIA Reflex ignore this.",
            Options = new()
            {
                O("Off", new SettingWrite(PreRenderLimit, 0), new SettingWrite(UllCplState, 0, true), new SettingWrite(UllEnabled, 0, true)),
                O("On", new SettingWrite(PreRenderLimit, 1), new SettingWrite(UllCplState, 1, true), new SettingWrite(UllEnabled, 0, true)),
                O("Ultra", new SettingWrite(PreRenderLimit, 1), new SettingWrite(UllCplState, 2, true), new SettingWrite(UllEnabled, 1, true)),
            },
        },
        new()
        {
            Key = "vsync", Label = "Vertical sync", PrimaryId = VSync,
            Help = "Use the game's own setting, force V-Sync off or on, or Fast Sync (no tearing, less lag than V-Sync).",
            Options = new()
            {
                V("Use the game's setting", VSync, 0x60925292),
                V("Off", VSync, 0x08416747),
                V("On", VSync, 0x47814940),
                V("Fast", VSync, 0x18888888),
            },
        },
        new()
        {
            Key = "power", Label = "Power management mode", PrimaryId = PowerMode,
            Help = "Prefer maximum performance keeps clocks up while the game runs, at the cost of power and heat.",
            Options = new()
            {
                V("Normal", PowerMode, 0),
                V("Optimal power", PowerMode, 5),
                V("Prefer maximum performance", PowerMode, 1),
            },
        },
        new()
        {
            Key = "texture", Label = "Texture filtering quality", PrimaryId = TextureQuality,
            Help = "High quality looks best; High performance trades a little sharpness for speed.",
            Options = new()
            {
                V("High performance", TextureQuality, 20),
                V("Performance", TextureQuality, 10),
                V("Quality", TextureQuality, 0),
                V("High quality", TextureQuality, 0xFFFFFFF6),
            },
        },
        new()
        {
            Key = "shadercache", Label = "Shader cache size", PrimaryId = ShaderCacheSize, GlobalOnly = true,
            Help = "How much disk space compiled shaders may use. Bigger means fewer stutters after updates.",
            Options = new()
            {
                V("Disabled", ShaderCacheSize, 0),
                V("128 MB", ShaderCacheSize, 128),
                V("256 MB", ShaderCacheSize, 256),
                V("512 MB", ShaderCacheSize, 512),
                V("1 GB", ShaderCacheSize, 1024),
                V("5 GB", ShaderCacheSize, 5120),
                V("10 GB", ShaderCacheSize, 10240),
                V("100 GB", ShaderCacheSize, 102400),
                V("Unlimited", ShaderCacheSize, 0xFFFFFFFF),
            },
        },
        new()
        {
            Key = "gsync", Label = "G-SYNC", PrimaryId = GsyncMode, GlobalOnly = true,
            Help = "Variable refresh rate on G-SYNC and G-SYNC Compatible displays.",
            Options = new()
            {
                V("Off", GsyncMode, 0),
                V("On (full screen)", GsyncMode, 1),
                V("On (full screen and windowed)", GsyncMode, 2),
            },
        },
        new()
        {
            Key = "gsyncapp", Label = "G-SYNC", PrimaryId = GsyncApp, Ids = new[] { GsyncApp, GsyncAppRequest }, GameOnly = true,
            Help = "Turn G-SYNC off for just this game (some games flicker or stutter with it).",
            Options = new()
            {
                O("On", new SettingWrite(GsyncApp, 0), new SettingWrite(GsyncAppRequest, 0, true)),
                O("Off", new SettingWrite(GsyncApp, 1), new SettingWrite(GsyncAppRequest, 1, true)),
            },
        },
    };

    public static readonly HashSet<uint> AllIds = All.SelectMany(d => d.AllIds).ToHashSet();

    public static bool IsOptional(uint id) => id is UllCplState or UllEnabled or GsyncAppRequest;

    public static string NameOf(uint id) => All.FirstOrDefault(d => d.AllIds.Contains(id))?.Label ?? $"setting 0x{id:X8}";
}
