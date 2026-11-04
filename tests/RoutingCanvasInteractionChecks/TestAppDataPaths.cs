using System.IO;

namespace Elka.VoiceMeeterFxHost.App;

// This test assembly must never write the user's real settings or runtime logs.
internal static class AppDataPaths
{
    internal static readonly string TestId = Guid.Parse(Environment.GetEnvironmentVariable("ELKA_SETTINGS_TEST_ID") ?? Guid.NewGuid().ToString()).ToString("N");
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "settings-checks", TestId);
    public static string RoamingRoot => Path.Combine(Root, "roaming");
    public static string LocalRoot => Path.Combine(Root, "local");
    public static string LegacyLocalRoot => Path.Combine(Root, "legacy");
}
