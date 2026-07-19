using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Foundry.FileIO;

namespace Foundry.IntegrationTests;

public class FileIOTests
{
    public record TestCsvRecord
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public string Email { get; set; } = "";
    }

    [Fact]
    public async Task CsvDataParser_ParsesValidCsvStream()
    {
        // Arrange
        var csvContent = "Name,Age,Email\nJohn Doe,30,john@example.com\nJane Doe,25,jane@example.com";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvContent));
        var parser = new CsvDataParser<TestCsvRecord>();

        // Act
        var records = new List<TestCsvRecord>();
        await foreach (var record in parser.ParseAsync(stream))
        {
            records.Add(record);
        }

        // Assert
        Assert.Equal(2, records.Count);
        Assert.Equal("John Doe", records[0].Name);
        Assert.Equal(30, records[0].Age);
        Assert.Equal("john@example.com", records[0].Email);
        Assert.Equal("Jane Doe", records[1].Name);
        Assert.Equal(25, records[1].Age);
        Assert.Equal("jane@example.com", records[1].Email);
    }

    [Fact]
    public async Task CsvDataExporter_WritesValidCsvStream()
    {
        // Arrange
        var records = new List<TestCsvRecord>
        {
            new() { Name = "Alice", Age = 28, Email = "alice@example.com" },
            new() { Name = "Bob", Age = 32, Email = "bob@example.com" }
        };

        async IAsyncEnumerable<TestCsvRecord> GetDataAsync()
        {
            foreach (var record in records)
            {
                yield return record;
                await Task.Yield();
            }
        }

        using var outputStream = new MemoryStream();
        var exporter = new CsvDataExporter<TestCsvRecord>();

        // Act
        await exporter.ExportAsync(GetDataAsync(), outputStream);
        outputStream.Position = 0;
        using var reader = new StreamReader(outputStream);
        var csvOutput = await reader.ReadToEndAsync();

        // Assert
        Assert.Contains("Name,Age,Email", csvOutput);
        Assert.Contains("Alice,28,alice@example.com", csvOutput);
        Assert.Contains("Bob,32,bob@example.com", csvOutput);
    }

    [Fact]
    public void FileSecurityValidator_VerifiesMimeMagicBytes()
    {
        // Arrange
        var validator = new FileSecurityValidator();
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        using var validPngStream = new MemoryStream(pngHeader);
        using var invalidPngStream = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x00 });

        // Act & Assert
        Assert.True(validator.VerifySignature("image.png", validPngStream));
        Assert.False(validator.VerifySignature("image.png", invalidPngStream));
        // Permitted extensions without headers (e.g. CSV) should return true
        Assert.True(validator.VerifySignature("data.csv", invalidPngStream));
    }

    [Fact]
    public void FileSecurityValidator_SanitizesFileName_Correctly()
    {
        // Arrange
        var validator = new FileSecurityValidator();

        // Act & Assert
        Assert.Equal("profile.png", validator.SanitizeFileName("../../profile.png"));
        Assert.Equal("data.csv", validator.SanitizeFileName("data/folder\\data.csv"));
        Assert.Equal("clean_file.txt", validator.SanitizeFileName("clean_file.txt"));
    }
}
