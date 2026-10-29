using Microsoft.Mxc.Sdk.V1;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Isolator.Mxc;

public static class MxcIsolationProperties
{
    internal const string Prefix = "Mxc.";
    public const string Filesystem = "Mxc.Filesystem";
    public const string Network = "Mxc.Network";
    public const string Ui = "Mxc.Ui";
    public const string Containment = "Mxc.Containment";
    public const string TimeoutMs = "Mxc.TimeoutMs";
    public const string ContainerName = "Mxc.ContainerName";
    public const string WorkingDirectory = "Mxc.WorkingDirectory";
    public const string Environment = "Mxc.Environment";
    public const string InheritDefaultEnvironment = "Mxc.InheritDefaultEnvironment";
}

public sealed class MxcIsolationHost : BaseIsolationHost
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly string _runtimeConfig = $"{{\r\n  \"runtimeOptions\": {{\r\n    \"tfm\": \"net10.0\",\r\n    \"framework\": {{\r\n      \"name\": \"Microsoft.NETCore.App\",\r\n      \"version\": \"10.0.0\"\r\n    }},\r\n    \"rollForward\": \"Minor\",\r\n    \"configProperties\": {{\r\n      \"System.GC.Server\": true,\r\n      \"System.GC.Concurrent\": true\r\n    }}\r\n  }}\r\n}}";

    private static readonly string _programSource = $$"""
        using System;
        using System.IO;
        using Isolator;
        using System.Reflection;

        internal class Program
        {
            public static void Main()
            {
                var args = Environment.GetCommandLineArgs();
                var envelope = IsolationHelper.Deserialize<PluginEnvelope>(File.ReadAllText(args[1]));
                _ = Assembly.LoadFrom(envelope.PluginAssemblyPath);
                var pluginType = Type.GetType(envelope.PluginType, throwOnError: true)!;
                var plugin = IsolationHelper.Deserialize(envelope.PluginJson, pluginType) as IPlugin
                    ?? throw new InvalidOperationException("Failed to deserialize plugin instance or not a plugin type.");
                var context = IsolationHelper.Deserialize<IsolationContext>(envelope.ContextJson);

                ExecutionResult response;
                using (var stdout = IsolationHelper.CaptureOutput())
                using (var stderr = IsolationHelper.CaptureError())
                {
                    var result = plugin.Execute(context);
                    response = new(
                        StandardOutput: stdout.ToString(),
                        StandardError: stderr.ToString(),
                        Result: result,
                        ResultType: result?.GetType()?.AssemblyQualifiedName,
                        Properties: context.Properties);
                }

                Console.Out.WriteLine(IsolationHelper.Serialize(response));
            }
        }
        """;

    public override async Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(
        TPlugin plugin,
        IsolationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var pluginAssemblyPath = plugin.GetType().Assembly.Location;
        if (string.IsNullOrWhiteSpace(pluginAssemblyPath) || !File.Exists(pluginAssemblyPath))
        {
            throw new InvalidOperationException("The plugin assembly must have a file location.");
        }

        var tempDir = CreateTempDirectory();
        try
        {
            var (outputDir, dllPath) = await CompileRunnerAsync(
                _programSource,
                tempDir.FullName,
                isApp: true,
                cancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(outputDir, $"{_runnerName}.runtimeconfig.json"),
                _runtimeConfig,
                cancellationToken);

            CopyIsolationAssembly(outputDir);

            var envelopePath = Path.Combine(outputDir, "Plugin.json");
            var envelope = new PluginEnvelope(
                PluginType: plugin.GetType().AssemblyQualifiedName!,
                PluginJson: IsolationHelper.Serialize(plugin),
                PluginAssemblyPath: pluginAssemblyPath,
                ContextJson: IsolationHelper.Serialize(context));
            await File.WriteAllTextAsync(
                envelopePath,
                IsolationHelper.Serialize(envelope),
                cancellationToken);

            var filesystem = GetProperty<FilesystemPolicy>(context, MxcIsolationProperties.Filesystem)
                ?? new FilesystemPolicy();
            filesystem = CopyFilesystemPolicy(filesystem);
            AddReadOnlyPath(filesystem, outputDir);
            var dotnetPath = GetDotnetPath();
            AddReadOnlyPath(filesystem, Path.GetDirectoryName(dotnetPath)!);
            var pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath);
            if (pluginDirectory is not null)
            {
                AddReadOnlyPath(filesystem, pluginDirectory);
            }

            var request = new ContainerRequest($"{QuoteCommandArgument(dotnetPath)} {QuoteCommandArgument(dllPath)} {QuoteCommandArgument(envelopePath)}")
            {
                Containment = GetProperty<Containment>(context, MxcIsolationProperties.Containment) ?? new Containment.Process(),
                Filesystem = filesystem,
                Network = GetProperty<NetworkPolicy>(context, MxcIsolationProperties.Network),
                Ui = GetProperty<UiPolicy>(context, MxcIsolationProperties.Ui),
                TimeoutMs = GetProperty<uint?>(context, MxcIsolationProperties.TimeoutMs),
                ContainerName = GetProperty<string>(context, MxcIsolationProperties.ContainerName),
                WorkingDirectory = GetProperty<string>(context, MxcIsolationProperties.WorkingDirectory) ?? outputDir,
                Environment = GetProperty<Dictionary<string, string>>(context, MxcIsolationProperties.Environment),
                InheritDefaultEnvironment = GetProperty<bool>(context, MxcIsolationProperties.InheritDefaultEnvironment),
            };

            using var process = await MxcContainer.SpawnAsync(request, cancellationToken: cancellationToken);

            using var stdoutReader = process.StandardOutput is { } standardOutput
                ? new StreamReader(standardOutput)
                : null;
            using var stderrReader = process.StandardError is { } standardError
                ? new StreamReader(standardError)
                : null;

            var stdoutTask = stdoutReader?.ReadToEndAsync(cancellationToken) ?? Task.FromResult(string.Empty);
            var stderrTask = stderrReader?.ReadToEndAsync(cancellationToken) ?? Task.FromResult(string.Empty);

            WaitResult runResult;
            string stdout;
            string stderr;

            try
            {
                runResult = await process.WaitAsync(cancellationToken);
                var output = await Task.WhenAll(stdoutTask, stderrTask);
                stdout = output[0];
                stderr = output[1];
            }
            finally
            {
                try
                {
                    if (!process.TryGetExitCode(out _))
                    {
                        process.Kill();
                    }
                }
                finally
                {
                    await process.WaitAsync(CancellationToken.None);
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
            }

            if (runResult.TimedOut)
            {
                throw new TimeoutException("MXC terminated the plugin after its execution timeout.");
            }
            if (runResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"MXC plugin process exited with code {runResult.ExitCode}: {stderr}");
            }

            var execution = IsolationHelper.Deserialize<ExecutionResult>(stdout);
            foreach (var (key, value) in execution.Properties)
            {
                if (!key.StartsWith(MxcIsolationProperties.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    context.Properties[key] = value;
                }
            }

            var result = execution.Result;
            if (result is not null && !string.IsNullOrWhiteSpace(execution.ResultType))
            {
                result = IsolationHelper.Deserialize(result, Type.GetType(execution.ResultType)!);
            }

            return new PluginExecutionResult(execution.StandardOutput, execution.StandardError, result);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static T? GetProperty<T>(IsolationContext context, string name)
    {
        if (!context.Properties.TryGetValue(name, out var value) || value is null)
        {
            return default;
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        if (value is JsonElement element)
        {
            return element.Deserialize<T>();
        }

        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
    }

    private static FilesystemPolicy CopyFilesystemPolicy(FilesystemPolicy filesystem) =>
        new()
        {
            ReadwritePaths = [.. filesystem.ReadwritePaths],
            ReadonlyPaths = [.. filesystem.ReadonlyPaths],
            DeniedPaths = [.. filesystem.DeniedPaths],
            ClearPolicyOnExit = filesystem.ClearPolicyOnExit,
        };

    private static void AddReadOnlyPath(FilesystemPolicy filesystem, string path)
    {
        if (filesystem.ReadwritePaths.Any(existingPath => PathsOverlap(existingPath, path)) ||
            filesystem.DeniedPaths.Any(deniedPath => IsPathWithin(path, deniedPath)) ||
            filesystem.ReadonlyPaths.Any(existingPath => IsPathWithin(path, existingPath)))
        {
            return;
        }

        filesystem.ReadonlyPaths.Add(path);
    }

    private static string GetDotnetPath()
    {
        var dotnetRoot = Path.GetFullPath(Path.Combine(
            RuntimeEnvironment.GetRuntimeDirectory(),
            "..",
            "..",
            ".."));
        var dotnetPath = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnetPath))
        {
            throw new InvalidOperationException("Could not locate the .NET host for the current runtime.");
        }

        return dotnetPath;
    }

    private static bool PathsOverlap(string firstPath, string secondPath) =>
        IsPathWithin(firstPath, secondPath) || IsPathWithin(secondPath, firstPath);

    private static bool IsPathWithin(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return _pathComparer.Equals(relativePath, ".") ||
            (!Path.IsPathRooted(relativePath) &&
             !_pathComparer.Equals(relativePath, "..") &&
             !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string QuoteCommandArgument(string argument) =>
        OperatingSystem.IsWindows()
            ? $"\"{argument}\""
            : $"'{argument.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
