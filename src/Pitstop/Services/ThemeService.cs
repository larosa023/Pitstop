using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Pitstop.Services;

/// <summary>A suite theme: the same 14 presets and the same JSON file format as Hangar (docs/THEMES.md).</summary>
public sealed class ThemeDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Dark { get; set; } = true;
    /// <summary>mica | acrylic | none</summary>
    public string Backdrop { get; set; } = "none";
    public double Transparency { get; set; }
    public double SurfaceOpacity { get; set; } = 1;
    public uint Bg, Sidebar, Surface, SurfaceHover, SurfaceStrong, Text, Subtext, Faint;
    /// <summary>0 = follow the Windows accent color.</summary>
    public uint Accent;
    public bool FollowWindows { get; set; }
    public bool Custom { get; set; }
    public string? File { get; set; }

    public override string ToString() => Name;
}

public enum BackdropKind { None, Mica, Acrylic }

public sealed class ThemeService
{
    public static readonly uint[] AccentSwatches = { 0x4F8CFF, 0x9B6CF2, 0xE86A92, 0xFF5D5D, 0xFF8A4C, 0xF2B84B, 0x3FB98E, 0x2EC4C4, 0xA0A0AA };

    private static ThemeDef Def(string id, string name, string desc, bool dark, string backdrop, double transparency,
        uint bg, uint sidebar, uint surface, uint hover, uint strong, uint text, uint subtext, uint faint, uint accent) => new()
    {
        Id = id, Name = name, Description = desc, Dark = dark, Backdrop = backdrop, Transparency = transparency,
        Bg = bg, Sidebar = sidebar, Surface = surface, SurfaceHover = hover, SurfaceStrong = strong,
        Text = text, Subtext = subtext, Faint = faint, Accent = accent,
    };

    // Exactly Hangar's built-ins (src/themes.cpp).
    public static readonly IReadOnlyList<ThemeDef> BuiltIn = BuildBuiltIns();

    private static List<ThemeDef> BuildBuiltIns()
    {
        var list = new List<ThemeDef>
        {
            Def("dark", "Dark", "The default", true, "mica", 0.20, 0x16161A, 0x111114, 0x222228, 0x2A2A31, 0x34343C, 0xF2F2F5, 0xA8A8B3, 0x6E6E7A, 0),
            Def("light", "Light", "Bright and clean", false, "mica", 0.20, 0xF5F5F7, 0xEBEBEF, 0xFFFFFF, 0xF7F7FA, 0xE6E6EC, 0x16161A, 0x5C5C68, 0x8E8E9A, 0),
        };
        var sys = Def("system", "Windows", "Light or dark, like Windows", true, "mica", 0.20, 0x16161A, 0x111114, 0x222228, 0x2A2A31, 0x34343C, 0xF2F2F5, 0xA8A8B3, 0x6E6E7A, 0);
        sys.FollowWindows = true;
        list.Add(sys);
        list.Add(Def("midnight", "Midnight", "Pure black for OLED screens", true, "none", 0.0, 0x000000, 0x000000, 0x0E0E10, 0x16161A, 0x202025, 0xF5F5F7, 0xA0A0AA, 0x64646E, 0x7AA2FF));
        var glass = Def("glass", "Glass", "See-through, over your wallpaper", true, "acrylic", 0.55, 0x141418, 0x0E0E12, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xF7F7FA, 0xC2C2CC, 0x8A8A96, 0);
        glass.SurfaceOpacity = 0.07;
        list.Add(glass);
        list.Add(Def("frost", "Frost", "Cool arctic blues", true, "mica", 0.12, 0x2E3440, 0x272C36, 0x3B4252, 0x434C5E, 0x4C566A, 0xECEFF4, 0xC0C8D6, 0x7B8698, 0x88C0D0));
        list.Add(Def("twilight", "Twilight", "Deep violet", true, "mica", 0.12, 0x1E1B2E, 0x19162A, 0x2A2640, 0x332E4D, 0x3E3859, 0xEEEAF7, 0xB4ADC9, 0x7D7596, 0xB794F6));
        list.Add(Def("forest", "Forest", "Evergreen and calm", true, "mica", 0.12, 0x141B17, 0x101612, 0x1D2721, 0x24302A, 0x2E3B33, 0xE8F0EA, 0xA7B8AD, 0x6C7F72, 0x5FD08B));
        list.Add(Def("sunset", "Sunset", "Warm and cozy", true, "mica", 0.12, 0x1C1614, 0x17110F, 0x2A201C, 0x332722, 0x3E302A, 0xF6EEE9, 0xC4B3A9, 0x8B776C, 0xFF8A4C));
        list.Add(Def("ocean", "Ocean", "Deep teal", true, "mica", 0.12, 0x0F1A20, 0x0B151A, 0x16252D, 0x1C2E37, 0x243843, 0xE6F1F5, 0xA3BAC4, 0x66808C, 0x3CC8C8));
        list.Add(Def("rose", "Rose", "Soft pink on plum", true, "mica", 0.12, 0x1D1519, 0x181115, 0x2A1F25, 0x33262D, 0x3E2E37, 0xF7ECF1, 0xC7AFBA, 0x8D7380, 0xFF7AA8));
        list.Add(Def("graphite", "Graphite", "Monochrome, no color", true, "none", 0.0, 0x1A1A1A, 0x141414, 0x262626, 0x2E2E2E, 0x3A3A3A, 0xF0F0F0, 0xABABAB, 0x707070, 0xE6E6E6));
        list.Add(Def("paper", "Paper", "Warm light, easy on the eyes", false, "none", 0.0, 0xF4EFE6, 0xEAE3D6, 0xFBF8F2, 0xF6F1E7, 0xE5DCCB, 0x2B241B, 0x6B5F50, 0x9A8E7E, 0xB5651D));
        list.Add(Def("mist", "Mist", "Cool light gray-blue", false, "mica", 0.15, 0xEEF2F6, 0xE3E9EF, 0xFAFCFE, 0xF3F7FA, 0xDCE4EC, 0x17202A, 0x56636F, 0x8A97A3, 0x2F7DD1));
        return list;
    }

    public List<ThemeDef> Custom { get; } = new();
    public IEnumerable<ThemeDef> All => BuiltIn.Concat(Custom);
    public List<string> LoadErrors { get; } = new();

    public ThemeDef Current { get; private set; } = BuiltIn[0];
    public BackdropKind Backdrop { get; private set; }
    public double WindowOpacity { get; private set; } = 1;
    public Color BgColor { get; private set; }

    /// <summary>Raised after every apply, so windows can update their backdrop.</summary>
    public event Action? Applied;

    public ThemeService() => ReloadCustom();

    public void ReloadCustom()
    {
        Custom.Clear();
        LoadErrors.Clear();
        foreach (var dir in new[] { Paths.Themes, Paths.HangarThemes })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    if (TryParse(System.IO.File.ReadAllText(file), out var t, out var err))
                    {
                        t!.File = file;
                        if (!Custom.Any(c => c.Id == t.Id)) Custom.Add(t);
                    }
                    else
                    {
                        LoadErrors.Add($"{Path.GetFileName(file)}: {err}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"themes in {dir}: {ex.Message}");
            }
        }
        Custom.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public ThemeDef Find(string id) => All.FirstOrDefault(t => t.Id == id) ?? BuiltIn[0];

    // ---------------------------------------------------------------- Hangar JSON format

    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static bool TryParse(string text, out ThemeDef? theme, out string error)
    {
        theme = null;
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(text, JsonOptions);
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object) { error = "not valid JSON"; return false; }
            string Str(JsonElement e, string key, string def = "") =>
                e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? def : def;
            double Num(string key, double def) =>
                v.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : def;

            var name = Str(v, "name").Trim();
            if (name.Length == 0) { error = "missing \"name\""; return false; }
            bool dark = !Str(v, "mode", "dark").Equals("light", StringComparison.OrdinalIgnoreCase);
            var backdrop = Str(v, "backdrop", "none").ToLowerInvariant();
            if (backdrop != "mica" && backdrop != "acrylic") backdrop = "none";
            var b = BuiltIn[dark ? 0 : 1];
            var t = new ThemeDef
            {
                Id = "custom:" + Str(v, "id", name).ToLowerInvariant(),
                Name = name,
                Description = Str(v, "description", "Your theme"),
                Dark = dark,
                Backdrop = backdrop,
                Transparency = Math.Clamp(Num("transparency", 0.15), 0, 0.9),
                SurfaceOpacity = Math.Clamp(Num("surfaceOpacity", 1), 0.02, 1),
                Bg = b.Bg, Sidebar = b.Sidebar, Surface = b.Surface, SurfaceHover = b.SurfaceHover, SurfaceStrong = b.SurfaceStrong,
                Text = b.Text, Subtext = b.Subtext, Faint = b.Faint, Accent = 0,
                Custom = true,
            };
            if (v.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                bool Read(string key, ref uint field)
                {
                    var s = Str(c, key);
                    return s.Length == 0 || TryHex(s, out field);
                }
                if (!Read("background", ref t.Bg)) { error = "bad color for background"; return false; }
                if (!Read("sidebar", ref t.Sidebar)) { error = "bad color for sidebar"; return false; }
                if (!Read("surface", ref t.Surface)) { error = "bad color for surface"; return false; }
                if (!Read("surfaceHover", ref t.SurfaceHover)) { error = "bad color for surfaceHover"; return false; }
                if (!Read("surfaceStrong", ref t.SurfaceStrong)) { error = "bad color for surfaceStrong"; return false; }
                if (!Read("text", ref t.Text)) { error = "bad color for text"; return false; }
                if (!Read("subtext", ref t.Subtext)) { error = "bad color for subtext"; return false; }
                if (!Read("faint", ref t.Faint)) { error = "bad color for faint"; return false; }
                var a = Str(c, "accent", "windows").ToLowerInvariant();
                if (a != "windows")
                {
                    if (!TryHex(a, out var acc)) { error = "bad accent color"; return false; }
                    t.Accent = acc == 0 ? 0x010101u : acc;   // pure black is a real choice
                }
            }
            theme = t;
            return true;
        }
        catch (JsonException)
        {
            error = "not valid JSON";
            return false;
        }
    }

    public static bool TryHex(string s, out uint value)
    {
        value = 0;
        s = s.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public static string Hex(uint c) => $"#{c & 0xFFFFFF:X6}";

    public string ToJson(ThemeDef t, string accentSetting)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("name", t.Custom ? t.Name : t.Name + " (copy)");
            w.WriteString("description", t.Description);
            w.WriteString("mode", t.Dark ? "dark" : "light");
            w.WriteString("backdrop", t.Backdrop);
            w.WriteNumber("transparency", Math.Round(t.Transparency, 2));
            if (t.SurfaceOpacity < 1) w.WriteNumber("surfaceOpacity", Math.Round(t.SurfaceOpacity, 2));
            w.WriteStartObject("colors");
            w.WriteString("background", Hex(t.Bg));
            w.WriteString("sidebar", Hex(t.Sidebar));
            w.WriteString("surface", Hex(t.Surface));
            w.WriteString("surfaceHover", Hex(t.SurfaceHover));
            w.WriteString("surfaceStrong", Hex(t.SurfaceStrong));
            w.WriteString("text", Hex(t.Text));
            w.WriteString("subtext", Hex(t.Subtext));
            w.WriteString("faint", Hex(t.Faint));
            bool windows = accentSetting == "windows" || (accentSetting == "theme" && t.Accent == 0);
            w.WriteString("accent", windows ? "windows" : Hex(ResolveAccent(t, accentSetting)));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Writes the current theme to the themes folder; returns the file path.</summary>
    public string SaveCurrentAsFile(string accentSetting)
    {
        Directory.CreateDirectory(Paths.Themes);
        var baseName = string.Concat((Current.Custom ? Current.Name : Current.Name + " copy").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch));
        var path = Path.Combine(Paths.Themes, baseName + ".json");
        for (int i = 2; System.IO.File.Exists(path); i++) path = Path.Combine(Paths.Themes, $"{baseName} {i}.json");
        System.IO.File.WriteAllText(path, ToJson(Current, accentSetting));
        return path;
    }

    // ---------------------------------------------------------------- applying

    public static uint WindowsAccent()
    {
        try
        {
            var c = Application.Current?.PlatformSettings?.GetColorValues().AccentColor1;
            if (c is { } col && (col.R | col.G | col.B) != 0) return (uint)(col.R << 16 | col.G << 8 | col.B);
        }
        catch { }
        return 0x4F8CFF;
    }

    public static bool WindowsIsLight()
    {
        try
        {
            return Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light;
        }
        catch
        {
            return false;
        }
    }

    public static uint ResolveAccent(ThemeDef def, string accentSetting)
    {
        if (accentSetting.StartsWith('#') && TryHex(accentSetting, out var c)) return c;
        if (accentSetting == "theme" && def.Accent != 0) return def.Accent & 0xFFFFFF;
        return WindowsAccent();
    }

    private static bool BackdropSupported() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private static Color Rgb(uint c, double a = 1) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    public void Apply(string themeId, string accentSetting)
    {
        var app = Application.Current;
        if (app is null) return;
        var def0 = Find(themeId);
        Current = def0;
        var def = def0.FollowWindows ? BuiltIn[WindowsIsLight() ? 1 : 0] : def0;

        bool wantsBackdrop = def.Backdrop != "none";
        bool backdrop = wantsBackdrop && BackdropSupported();
        Backdrop = !backdrop ? BackdropKind.None : def.Backdrop == "acrylic" ? BackdropKind.Acrylic : BackdropKind.Mica;

        var accent = Rgb(ResolveAccent(def, def0.FollowWindows ? (accentSetting == "theme" ? "windows" : accentSetting) : accentSetting));
        // keep the accent readable on the background
        if (def.Dark && Luma(accent) < 0.30) accent = Mix(accent, Colors.White, 0.35);
        if (!def.Dark && Luma(accent) > 0.55) accent = Mix(accent, Colors.Black, 0.30);
        var accentHover = def.Dark ? Mix(accent, Colors.White, 0.12) : Mix(accent, Colors.Black, 0.10);
        var onAccent = Luma(accent) > 0.55 ? Color.FromRgb(0x10, 0x10, 0x14) : Colors.White;

        double tr = backdrop ? def.Transparency : 0;
        var bg = Rgb(def.Bg, 1 - tr);
        BgColor = bg;
        WindowOpacity = 1 - tr;
        var sidebar = Rgb(def.Sidebar, backdrop ? Math.Max(0.25, 1 - tr * 2.2) : 1);
        Color surface, hover, strong;
        if (def.SurfaceOpacity < 1)
        {
            double o = def.SurfaceOpacity;
            Color Layer(uint c, double k) => backdrop ? Rgb(c, Math.Min(1, o * k)) : Mix(Rgb(def.Bg), Rgb(c), Math.Min(1, o * k));
            surface = Layer(def.Surface, 1.0);
            hover = Layer(def.SurfaceHover, 1.6);
            strong = Layer(def.SurfaceStrong, 2.3);
        }
        else
        {
            surface = Rgb(def.Surface);
            hover = Rgb(def.SurfaceHover);
            strong = Rgb(def.SurfaceStrong);
        }
        var divider = def.Dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0);
        Color good, warn, bad, info;
        if (def.Dark) { good = Rgb(0x5BD18B); warn = Rgb(0xF2B84B); bad = Rgb(0xFF6B6B); info = Rgb(0x6CB4FF); }
        else { good = Rgb(0x1F9D57); warn = Rgb(0xB7791F); bad = Rgb(0xD64545); info = Rgb(0x2F6FDB); }
        if (def.Accent != 0 && accentSetting == "theme") info = accent;

        var r = app.Resources;
        r["Bg"] = new SolidColorBrush(bg);
        r["Sidebar"] = new SolidColorBrush(sidebar);
        r["Surface"] = new SolidColorBrush(surface);
        r["SurfaceAlt"] = new SolidColorBrush(hover);
        r["SurfaceStrong"] = new SolidColorBrush(strong);
        r["Stroke"] = new SolidColorBrush(divider);
        r["Text"] = new SolidColorBrush(Rgb(def.Text));
        r["TextMuted"] = new SolidColorBrush(Rgb(def.Subtext));
        r["Faint"] = new SolidColorBrush(Rgb(def.Faint));
        r["Accent"] = new SolidColorBrush(accent);
        r["AccentHover"] = new SolidColorBrush(accentHover);
        r["AccentSoft"] = new SolidColorBrush(accent, 0.16);
        r["OnAccent"] = new SolidColorBrush(onAccent);
        r["Good"] = new SolidColorBrush(good);
        r["GoodSoft"] = new SolidColorBrush(good, 0.16);
        r["Warn"] = new SolidColorBrush(warn);
        r["WarnSoft"] = new SolidColorBrush(warn, 0.16);
        r["Hot"] = new SolidColorBrush(bad);
        r["HotSoft"] = new SolidColorBrush(bad, 0.16);
        r["Info"] = new SolidColorBrush(info);
        r["ToastBg"] = new SolidColorBrush(Mix(Rgb(def.Bg), Rgb(def.Surface), 0.8));

        // Make Fluent's own controls (toggles, accent buttons, focus rings, combo boxes) follow the theme.
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.15);
        r["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.30);
        r["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.45);
        r["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.15);
        r["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.30);
        r["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.45);
        var onAccentBrush = new SolidColorBrush(onAccent);
        r["AccentButtonForeground"] = onAccentBrush;
        r["AccentButtonForegroundPointerOver"] = onAccentBrush;
        r["AccentButtonForegroundPressed"] = onAccentBrush;
        r["ToggleSwitchKnobFillOn"] = onAccentBrush;
        r["ToggleSwitchKnobFillOnPointerOver"] = onAccentBrush;
        r["ToggleSwitchKnobFillOnPressed"] = onAccentBrush;
        r["CheckBoxCheckGlyphForegroundChecked"] = onAccentBrush;
        r["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = onAccentBrush;
        r["ComboBoxDropDownBackground"] = new SolidColorBrush(Mix(Rgb(def.Bg), Rgb(def.Surface), 0.7));
        r["FlyoutPresenterBackground"] = new SolidColorBrush(Mix(Rgb(def.Bg), Rgb(def.Surface), 0.7));
        r["MenuFlyoutPresenterBackground"] = new SolidColorBrush(Mix(Rgb(def.Bg), Rgb(def.Surface), 0.7));

        app.RequestedThemeVariant = def.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Applied?.Invoke();
    }

    /// <summary>Puts the current backdrop on a window (Mica / Acrylic on Windows 11, solid elsewhere).</summary>
    public void ApplyTo(Window w)
    {
        w.TransparencyLevelHint = Backdrop switch
        {
            BackdropKind.Mica => new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None },
            BackdropKind.Acrylic => new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.None },
            _ => new[] { WindowTransparencyLevel.None },
        };
        // If Windows refuses the backdrop (e.g. transparency effects off), fall back to a solid background.
        var bg = BgColor;
        if (Backdrop != BackdropKind.None && w.ActualTransparencyLevel == WindowTransparencyLevel.None)
            bg = Color.FromRgb(bg.R, bg.G, bg.B);
        w.Background = new SolidColorBrush(bg);
    }
}
