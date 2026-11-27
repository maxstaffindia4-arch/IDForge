using System.Reflection;

namespace IDForge;

/// <summary>Product identity strings shown in the About dialog — single place to edit.</summary>
public static class AppInfo
{
    public const string CreatorName = "Mehdi Hasan Sakib";

    /// <summary>FIX 4: the full build version, identical everywhere it appears
    /// (title bar, status bar, About). InformationalVersion carries the build
    /// metadata; falls back to the assembly version.</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                is AssemblyInformationalVersionAttribute[] attrs && attrs.Length > 0
                ? attrs[0].InformationalVersion : null;
            var v = info ?? asm.GetName().Version?.ToString(3) ?? "?";
            // Displayed version is one number — drop the "+commit" metadata
            // the SDK appends to InformationalVersion.
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }
}
