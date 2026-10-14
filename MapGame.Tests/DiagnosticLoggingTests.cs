using System.Text.Json;
using MapGame;
using Microsoft.Extensions.Logging;

namespace MapGame.Tests;

public sealed class DiagnosticLoggingTests
{
    [Fact]
    public void DiagnosticFileLogger_WritesTimestampCategoryMessageAndExceptionWithoutWaitingForExit()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mapgame-log-test-{Guid.NewGuid():N}");
        try
        {
            using var provider = new DiagnosticFileLogger(directory);
            var logger = provider.CreateLogger("MapGame.Tests");
            logger.LogInformation("HTTP status={Status}", 403);
            logger.LogError(new InvalidOperationException("Test failure"), "Socket rejected");
            using var reader = new StreamReader(new FileStream(Directory.GetFiles(directory).Single(),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var lines = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            using var info = JsonDocument.Parse(lines[0]);
            Assert.True(info.RootElement.GetProperty("timestampUtc").TryGetDateTimeOffset(out _));
            Assert.Equal("MapGame.Tests", info.RootElement.GetProperty("category").GetString());
            Assert.Equal("HTTP status=403", info.RootElement.GetProperty("message").GetString());
            using var error = JsonDocument.Parse(lines[1]);
            Assert.Contains("Test failure", error.RootElement.GetProperty("exception").GetString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }

    }

    [Fact]
    public void DiagnosticFileLogger_RotatesAndRetainsOnlyThreeParts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mapgame-log-test-{Guid.NewGuid():N}");
        try
        {
            using (var provider = new DiagnosticFileLogger(directory))
            {
                var logger = provider.CreateLogger("MapGame.Tests");
                for (var index = 0; index < 10; index++)
                {
                    logger.LogInformation("{Message}", new string('a', 2 * 1024 * 1024));
                }
            }
            var files = Directory.GetFiles(directory);
            Assert.Equal(3, files.Length);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 5 * 1024 * 1024));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
