using System.IO;
using System.IO.Compression;
using System.Text.Json;
using DKEzLogoMaker.Models;

namespace DKEzLogoMaker.Services;

public sealed class ProjectService
{
    private const string ProjectJsonName = "project.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public void Save(string path, LogoProject project)
    {
        project.Version = LogoProject.CurrentVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        if (project.Image.RuntimeBytes is { Length: > 0 } bytes)
        {
            var extension = Path.GetExtension(project.Image.SourceFileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".png";

            project.Image.EmbeddedAssetName = $"assets/image{extension.ToLowerInvariant()}";
            var assetEntry = zip.CreateEntry(project.Image.EmbeddedAssetName, CompressionLevel.Optimal);
            using var assetStream = assetEntry.Open();
            assetStream.Write(bytes, 0, bytes.Length);
        }
        else
        {
            project.Image.EmbeddedAssetName = null;
        }

        var jsonEntry = zip.CreateEntry(ProjectJsonName, CompressionLevel.Optimal);
        using var jsonStream = jsonEntry.Open();
        JsonSerializer.Serialize(jsonStream, project, JsonOptions);
    }

    public LogoProject Load(string path)
    {
        using var file = File.OpenRead(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var jsonEntry = zip.GetEntry(ProjectJsonName)
            ?? throw new InvalidDataException("project.json이 없는 프로젝트 파일입니다.");

        LogoProject project;
        using (var jsonStream = jsonEntry.Open())
        {
            project = JsonSerializer.Deserialize<LogoProject>(jsonStream, JsonOptions)
                ?? throw new InvalidDataException("프로젝트 정보를 읽을 수 없습니다.");
        }

        project.UpgradeIfNeeded();

        if (!string.IsNullOrWhiteSpace(project.Image.EmbeddedAssetName))
        {
            var assetEntry = zip.GetEntry(project.Image.EmbeddedAssetName);
            if (assetEntry is not null)
            {
                using var assetStream = assetEntry.Open();
                using var memory = new MemoryStream();
                assetStream.CopyTo(memory);
                project.Image.RuntimeBytes = memory.ToArray();
            }
        }

        return project;
    }
}
