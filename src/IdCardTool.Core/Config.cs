namespace IdCardTool.Core;

/// <summary>Card geometry and fixed values from the PSD template (637x1012 px).</summary>
public static class Config
{
    public const int CardWidth = 637;
    public const int CardHeight = 1012;

    // Photo window
    public const int PhotoX = 162;
    public const int PhotoY = 189;
    public const int PhotoW = 313;
    public const int PhotoH = 382;
    public const float PhotoCornerRadius = 20f;

    // Text boxes: (left, top, width, height) and center-x
    public const float PillY = 546f;
    public const float PillH = 34f;
    public const float PillMinW = 180f;
    public const float CenterX = 319f;

    public const float NameY = 592f;
    public const float NameH = 39f;
    public const float NameBoxW = 427f;
    public const float NameMaxSize = 50f;
    public const float NameMinSize = 34f;

    public const float PositionY = 650f;
    public const float PositionH = 25f;
    public const float PositionW = 278f;

    public const float IdY = 695f;
    public const float IdH = 23f;

    public const int BarcodeX = 34;
    public const int BarcodeY = 759;
    public const int BarcodeW = 570;
    public const int BarcodeH = 70;

    public const float ValidityY = 850f;
    public const float ValidityH = 19f;

    // Colors
    public static readonly SkiaSharp.SKColor Ink = new(0x1A, 0x25, 0x3C);      // #1A253C
    public static readonly SkiaSharp.SKColor Accent = new(0xA1, 0x2A, 0x26);   // #A12A26
    public static readonly SkiaSharp.SKColor White = SkiaSharp.SKColors.White;

    public static readonly string[] Positions =
    {
        "Junior Executive", "General Executive", "Technical Executive",
        "Supervisor", "Manager", "HR Manager", "Intern"
    };

    public static readonly string[] BloodGroups =
    {
        "A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-", "N/A"
    };

    public static readonly string[] ContractTypes = { "Contractual", "Permanent" };

    public const int ContractualMonths = 6;
    public const int PermanentMonths = 12;

    // Photo auto-fit (calibrate in Phase 0b)
    public const float FaceHeightRatio = 0.58f;        // face height as fraction of window height
    public const float HeadTopRatio = 0.08f;           // head top as fraction of window height
    public const float HairAboveFaceRatio = 0.55f;     // hair above YuNet face box, as fraction of face height
    public const float TransparentSkipThreshold = 0.05f; // >5% transparent px => already cut out
    public const byte WhiteBgMinChannel = 235;   // near-white flood fill threshold
    public const byte WhiteBgMaxDelta = 25;      // max channel spread = "low saturation"
    public const int MinPhotoShortSide = 600;
    public const long MaxPhotoBytes = 10 * 1024 * 1024;

    // Timezone for "today"
    public const string TimeZoneId = "Asia/Dhaka";

    public static readonly string AssetDir = ResolveAssetDir();

    private static string ResolveAssetDir()
    {
        // assets/ is copied to output dir; fall back to repo root for dev runs
        var local = Path.Combine(AppContext.BaseDirectory, "assets");
        if (Directory.Exists(local)) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "assets");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "background.png")))
                return candidate;
            dir = dir.Parent;
        }
        return local;
    }

    public static string BackgroundPath => Path.Combine(AssetDir, "background.png");
    public static string FontDir => Path.Combine(AssetDir, "fonts");
}
