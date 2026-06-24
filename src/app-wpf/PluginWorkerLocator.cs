using System.IO;
using System.Reflection;

namespace Elka.VoiceMeeterFxHost.App;

internal static class PluginWorkerLocator
{
    public const string EnvironmentVariable = "ELKA_PLUGIN_WORKER_EXE";

    private const string ResourcePrefix = "ElkaPluginWorker/";
    private const string WorkerExecutableName = "Elka.PluginWorker.exe";

    public static string ConfigureForCurrentProcess()
    {
        var sameDirectory = SameDirectoryWorkerPath();
        var workerPath = !string.IsNullOrWhiteSpace(sameDirectory) && File.Exists(sameDirectory)
            ? sameDirectory
            : ExtractEmbeddedWorker();

        if (string.IsNullOrWhiteSpace(workerPath))
        {
            workerPath = WorkerExecutablePath();
        }

        if (!string.IsNullOrWhiteSpace(workerPath) && File.Exists(workerPath))
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, workerPath, EnvironmentVariableTarget.Process);
            RuntimeLog.Write($"Plugin worker: {workerPath}");
            return workerPath;
        }

        Environment.SetEnvironmentVariable(EnvironmentVariable, string.Empty, EnvironmentVariableTarget.Process);
        RuntimeLog.Write("Plugin worker was not found beside the app and no embedded worker resource was available.");
        return string.Empty;
    }

    public static string WorkerExecutablePath()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var sameDirectory = SameDirectoryWorkerPath();
        if (!string.IsNullOrWhiteSpace(sameDirectory) && File.Exists(sameDirectory))
        {
            return sameDirectory;
        }

        var extracted = ExtractedWorkerPath();
        return File.Exists(extracted) ? extracted : string.Empty;
    }

    private static string ExtractEmbeddedWorker()
    {
        try
        {
            var assembly = typeof(PluginWorkerLocator).Assembly;
            var resourceNames = assembly
                .GetManifestResourceNames()
                .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .ToArray();

            if (resourceNames.Length == 0)
            {
                return string.Empty;
            }

            var directory = WorkerCacheDirectory();
            Directory.CreateDirectory(directory);

            foreach (var resourceName in resourceNames)
            {
                var fileName = resourceName[ResourcePrefix.Length..];
                if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    continue;
                }

                var destination = Path.Combine(directory, fileName);
                using var resource = assembly.GetManifestResourceStream(resourceName);
                if (resource is null)
                {
                    continue;
                }

                if (File.Exists(destination) && new FileInfo(destination).Length == resource.Length)
                {
                    continue;
                }

                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
                resource.CopyTo(output);
            }

            return ExtractedWorkerPath();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"Plugin worker extraction failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static string SameDirectoryWorkerPath()
    {
        var appPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return string.Empty;
        }

        var directory = Path.GetDirectoryName(appPath);
        return string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : Path.Combine(directory, WorkerExecutableName);
    }

    private static string ExtractedWorkerPath() => Path.Combine(WorkerCacheDirectory(), WorkerExecutableName);

    private static string WorkerCacheDirectory()
    {
        var version = typeof(PluginWorkerLocator).Assembly.GetName().Version?.ToString() ?? "current";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ElkaVoiceMeeterFxHost",
            "PluginWorker",
            version);
    }
}
