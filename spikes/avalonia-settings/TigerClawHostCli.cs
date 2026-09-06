using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace TigerClawSettingsSpike;

internal sealed class TigerClawHostCli
{
    private const string ExecutableOverrideEnvironmentVariable = "TIGERCLAW_SETTINGS_HOST_EXECUTABLE";
    private readonly string _executablePath;

    public TigerClawHostCli()
    {
        _executablePath = Environment.GetEnvironmentVariable(ExecutableOverrideEnvironmentVariable)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Input Methods",
                "TigerClawRealImeNativeAotHost.app",
                "Contents",
                "MacOS",
                "TigerClawRealImeNativeAotHost");
    }

    public string ExecutablePath => _executablePath;

    public SettingsSnapshot ReadSnapshot()
    {
        IReadOnlyDictionary<string, string> configuration = ParseConfiguration(Run("--tigerclaw-config", "show"));
        List<SchemaItem> schemas = ParseSchemas(Run("--tigerclaw-schema", "list"));
        List<UserDictionaryEntry> userEntries = ParseUserEntries(Run("--tigerclaw-user-dictionary", "list"));
        string selectedSchema = ParseConfiguration(Run("--tigerclaw-schema", "show"))
            .GetValueOrDefault("schema", string.Empty);
        string diagnostics = string.Join(Environment.NewLine, [
            "Host executable: " + _executablePath,
            "Configuration:",
            string.Join(Environment.NewLine, configuration.Select(pair => $"  {pair.Key}={pair.Value}")),
            "Schemas:",
            string.Join(Environment.NewLine, schemas.Select(schema => $"  {schema.Identifier}\t{schema.DisplayName}")),
            "User dictionary entries: " + userEntries.Count,
        ]);
        return new SettingsSnapshot(configuration, schemas, userEntries, selectedSchema, diagnostics);
    }

    public void SetConfiguration(string key, string value) => Run("--tigerclaw-config", "set", key, value);

    public string SelectionKeyConfigurationPath()
    {
        return ParseConfiguration(Run("--tigerclaw-selection-keys", "path"))
            .GetValueOrDefault("path")
            ?? throw new InvalidOperationException("输入法没有返回选词键配置文件路径。");
    }

    public string SelectionKeyConfigurationContents() => Run("--tigerclaw-selection-keys", "show");

    public void SetSelectionKeyConfiguration(string contents) => Run("--tigerclaw-selection-keys", "set", contents);

    public void ResetSelectionKeyConfiguration() => Run("--tigerclaw-selection-keys", "reset");

    public void SelectSchema(string identifier) => Run("--tigerclaw-schema", "select", identifier);

    public string SchemaDirectoryPath(string identifier) => ParseConfiguration(Run("--tigerclaw-schema", "path", identifier))
        .GetValueOrDefault("path")
        ?? throw new InvalidOperationException("输入法没有返回方案文件夹路径。");

    public void RedeploySchema() => Run("--tigerclaw-schema", "redeploy");

    public void RepairInputSourceRegistration() => Run("--tigerclaw-tis-phase", "repair");

    public void RenameSchema(string identifier, string displayName) => Run("--tigerclaw-schema", "rename", identifier, displayName);

    public void DeleteSchema(string identifier) => Run("--tigerclaw-schema", "delete", identifier);

    public string CreateSchemaExport(string identifier)
    {
        string output = Run("--tigerclaw-schema", "export", identifier);
        return ParseConfiguration(output).GetValueOrDefault("archive")
            ?? throw new InvalidOperationException("输入法没有返回导出文件路径。");
    }

    public void CleanupSchemaExport(string path) => Run("--tigerclaw-schema", "cleanup-export", path);

    public void ImportSchema(string path)
    {
        string stagingDirectory = Path.Combine(InputMethodDataRoot(), "import-staging", Guid.NewGuid().ToString("N"));
        string stagedPrimaryPath = StageSchemaImport(path, stagingDirectory);
        try
        {
            Run("--tigerclaw-schema", "import", stagedPrimaryPath);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public void AddUserEntry(string code, string text) => Run("--tigerclaw-user-dictionary", "add", code, text);

    public void RemoveUserEntry(string code, string text) => Run("--tigerclaw-user-dictionary", "remove", code, text);

    private string Run(params string[] arguments)
    {
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("未找到已安装的 TigerClaw 输入法。请先安装并启用开发版输入法。", _executablePath);
        }

        var startInfo = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 TigerClaw 输入法命令。");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(standardError) ? standardOutput.Trim() : standardError.Trim());
        }
        return standardOutput.Trim();
    }

    private static string StageSchemaImport(string sourcePath, string stagingDirectory)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("未找到要导入的码表。", sourcePath);
        }

        Directory.CreateDirectory(stagingDirectory);
        var copiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CopyWithDependencies(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!copiedPaths.Add(fullPath))
            {
                return;
            }

            string destination = Path.Combine(stagingDirectory, Path.GetFileName(fullPath));
            File.Copy(fullPath, destination, overwrite: false);
            foreach (string table in RimeImportTables(fullPath))
            {
                if (table.Contains('/') || table.Contains('\\') || table.Contains("..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"不支持的码表依赖：{table}");
                }
                string dependency = Path.Combine(Path.GetDirectoryName(fullPath)!, table + ".dict.yaml");
                if (!File.Exists(dependency))
                {
                    throw new FileNotFoundException($"缺少码表依赖：{Path.GetFileName(dependency)}", dependency);
                }
                CopyWithDependencies(dependency);
            }
        }

        try
        {
            string primaryPath = ResolvePrimarySchemaPath(sourcePath, stagingDirectory);
            CopyWithDependencies(primaryPath);
            foreach (string filename in new[] { "快符.txt", "常用符号.txt", "补充语料.txt" })
            {
                string companionPath = Path.Combine(Path.GetDirectoryName(primaryPath)!, filename);
                if (File.Exists(companionPath))
                {
                    File.Copy(companionPath, Path.Combine(stagingDirectory, filename), overwrite: false);
                }
            }
            if (primaryPath.EndsWith(".dict.yaml", StringComparison.OrdinalIgnoreCase))
            {
                string schemaPath = primaryPath[..^".dict.yaml".Length] + ".schema.yaml";
                if (File.Exists(schemaPath))
                {
                    File.Copy(schemaPath, Path.Combine(stagingDirectory, Path.GetFileName(schemaPath)), overwrite: false);
                }
            }
            return Path.Combine(stagingDirectory, Path.GetFileName(primaryPath));
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            throw;
        }
    }

    private static string ResolvePrimarySchemaPath(string sourcePath, string stagingDirectory)
    {
        if (!sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        string extractionDirectory = Path.Combine(stagingDirectory, "archive");
        ZipFile.ExtractToDirectory(sourcePath, extractionDirectory);
        List<string> dictionaries = Directory.EnumerateFiles(extractionDirectory, "*.dict.yaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}__MACOSX{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        List<string> primaryDictionaries = dictionaries.Where(path => RimeImportTables(path).Any()).ToList();
        if (primaryDictionaries.Count == 1)
        {
            return primaryDictionaries[0];
        }
        if (dictionaries.Count == 1)
        {
            return dictionaries[0];
        }

        List<string> textTables = Directory.EnumerateFiles(extractionDirectory, "*.txt", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}__MACOSX{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        if (textTables.Count == 1)
        {
            return textTables[0];
        }

        throw new InvalidDataException("压缩包中无法确定主码表；请解压后选择主 .dict.yaml 文件。");
    }

    private static IEnumerable<string> RimeImportTables(string path)
    {
        if (!path.EndsWith(".dict.yaml", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        bool readingImports = false;
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line == "...")
            {
                yield break;
            }
            if (line == "import_tables:")
            {
                readingImports = true;
                continue;
            }
            if (readingImports && line.StartsWith("- ", StringComparison.Ordinal))
            {
                string table = line[2..].Trim();
                if (!string.IsNullOrEmpty(table))
                {
                    yield return table;
                }
                continue;
            }
            if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
            {
                readingImports = false;
            }
        }
    }

    private static string InputMethodDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Application Support",
        "TigerClaw");

    private static IReadOnlyDictionary<string, string> ParseConfiguration(string output)
    {
        return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    private static List<SchemaItem> ParseSchemas(string output)
    {
        return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 3))
            .Where(parts => parts.Length >= 2)
            .Select(parts =>
            {
                string kind = parts.Length >= 3 ? parts[2] : string.Empty;
                return new SchemaItem(
                    parts[0],
                    parts[1],
                    kind.StartsWith("bundled", StringComparison.Ordinal),
                    string.Equals(kind, "imported", StringComparison.Ordinal) ||
                    string.Equals(kind, "bundled-editable", StringComparison.Ordinal));
            })
            .ToList();
    }

    private static List<UserDictionaryEntry> ParseUserEntries(string output)
    {
        return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new UserDictionaryEntry(parts[0], parts[1]))
            .ToList();
    }
}

internal sealed record SchemaItem(string Identifier, string DisplayName, bool IsBundled, bool IsEditable)
{
    public override string ToString() => DisplayName;
}

internal sealed record UserDictionaryEntry(string Code, string Text)
{
    public override string ToString() => $"{Code}    {Text}";
}

internal sealed record SettingsSnapshot(
    IReadOnlyDictionary<string, string> Configuration,
    List<SchemaItem> Schemas,
    List<UserDictionaryEntry> UserEntries,
    string SelectedSchema,
    string Diagnostics);
