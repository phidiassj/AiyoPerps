using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

internal sealed class DydxNodeBridge
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private readonly AppLogger _logger;

    public DydxNodeBridge(AppLogger logger)
    {
        _logger = logger;
    }

    public async Task<(bool IsSuccess, JsonElement Root, string Message)> RunAsync(
        string helperRoot,
        string command,
        object payload,
        CancellationToken cancellationToken)
    {
        var ready = await EnsureReadyAsync(helperRoot, cancellationToken);
        if (!ready.IsSuccess)
        {
            return (false, default, ready.Message);
        }

        var json = JsonSerializer.Serialize(payload);
        var fileName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var arguments = $"dydx-helper.js {command}";
        var result = await RunProcessAsync(fileName, arguments, helperRoot, json, cancellationToken);
        if (!result.IsSuccess)
        {
            return (false, default, result.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var root = doc.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("success", out var successNode) &&
                successNode.ValueKind == JsonValueKind.True)
            {
                return (true, root, "ok");
            }

            var message = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errorNode)
                ? errorNode.GetString() ?? "dYdX helper returned an error."
                : "dYdX helper returned an invalid response.";
            return (false, root, message);
        }
        catch (Exception ex)
        {
            _logger.Warn("dYdX", $"Helper output parse failed command={command}, output={Trim(result.Output)}, ex={ex.Message}");
            return (false, default, $"Failed to parse dYdX helper output: {ex.Message}");
        }
    }

    private async Task<(bool IsSuccess, string Message)> EnsureReadyAsync(string helperRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(helperRoot))
        {
            return (false, $"dYdX helper directory not found: {helperRoot}");
        }

        var scriptPath = Path.Combine(helperRoot, "dydx-helper.js");
        var packagePath = Path.Combine(helperRoot, "package.json");
        if (!File.Exists(scriptPath) || !File.Exists(packagePath))
        {
            return (false, "dYdX helper files are missing from the app output.");
        }

        var nodeModules = Path.Combine(helperRoot, "node_modules");
        if (Directory.Exists(nodeModules))
        {
            return EnsureNodeModulesHealth(nodeModules);
        }

        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(nodeModules))
            {
                return (true, "ok");
            }

            _logger.Info("dYdX", $"Installing node helper dependencies at {helperRoot}");
            var npmFile = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
            var install = await RunProcessAsync(
                npmFile,
                "install --omit=dev --ignore-scripts --no-fund --no-audit",
                helperRoot,
                null,
                cancellationToken);
            if (!install.IsSuccess)
            {
                return (false, install.Message);
            }

            if (!Directory.Exists(nodeModules))
            {
                return (false, "dYdX helper dependencies were not installed.");
            }

            return EnsureNodeModulesHealth(nodeModules);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private (bool IsSuccess, string Message) EnsureNodeModulesHealth(string nodeModulesRoot)
    {
        try
        {
            RepairNestedProtobufJs(nodeModulesRoot);
            return (true, "ok");
        }
        catch (Exception ex)
        {
            _logger.Warn("dYdX", $"Helper dependency repair failed: {ex.Message}");
            return (false, $"dYdX helper dependencies are incomplete: {ex.Message}");
        }
    }

    private void RepairNestedProtobufJs(string nodeModulesRoot)
    {
        var brokenPackageRoot = Path.Combine(nodeModulesRoot, "@confio", "ics23", "node_modules", "protobufjs");
        var targetIndex = Path.Combine(brokenPackageRoot, "src", "index-minimal.js");
        if (!Directory.Exists(brokenPackageRoot) || File.Exists(targetIndex))
        {
            return;
        }

        var sourceCandidates = new[]
        {
            Path.Combine(nodeModulesRoot, "@dydxprotocol", "v4-proto", "node_modules", "protobufjs", "src"),
            Path.Combine(nodeModulesRoot, "protobufjs", "src")
        };

        var sourceDirectory = Array.Find(sourceCandidates, Directory.Exists);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new DirectoryNotFoundException("Unable to find a healthy protobufjs/src directory for dYdX helper repair.");
        }

        var targetDirectory = Path.Combine(brokenPackageRoot, "src");
        CopyDirectory(sourceDirectory, targetDirectory);
        _logger.Info("dYdX", $"Repaired helper dependency: copied protobufjs/src into {brokenPackageRoot}");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var targetFile = Path.Combine(targetDirectory, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var childTarget = Path.Combine(targetDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, childTarget);
        }
    }

    private static async Task<(bool IsSuccess, string Output, string Message)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        string? stdin,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();
            if (!string.IsNullOrWhiteSpace(stdin))
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            }

            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                var message = !string.IsNullOrWhiteSpace(stderr)
                    ? Trim(stderr)
                    : $"Process exited with code {process.ExitCode}.";
                return (false, stdout, message);
            }

            return (true, stdout.Trim(), "ok");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string Trim(string text)
    {
        var value = text?.Trim() ?? string.Empty;
        return value.Length > 320 ? value[..320] : value;
    }
}
