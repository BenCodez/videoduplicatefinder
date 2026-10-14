using VDF.Core.Utils;

namespace VDF.Core.Tests.Utils;

[Collection("DatabaseUtils")]
public class TrashOriginalPathTests {
    [Fact]
    public void DirectoryTrashPreservesOriginalNameAndResolvesConflictsWithoutOverwriting() {
        string directory = Directory.CreateTempSubdirectory("vdf-directory-trash-test").FullName;
        try {
            string trash = Directory.CreateDirectory(Path.Combine(directory, "trash")).FullName;
            string original = Path.Combine(directory, "a video.mkv");
            string claim = Path.Combine(directory, ".vdf-private.holding");
            File.WriteAllBytes(claim, [1, 2, 3]);
            string existing = Path.Combine(trash, Path.GetFileName(original));
            File.WriteAllBytes(existing, [9]);
            Assert.True(FileUtils.MoveToTrashDirectory(claim, original, trash));
            Assert.False(File.Exists(claim));
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(existing));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(trash, "a video_1.mkv")));
            Assert.DoesNotContain(Directory.GetFiles(trash), path => path.Contains(".holding"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ClaimedSourceUsesOriginalNameAndEncodedRestorePath() {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("vdf-trash-test").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(directory, "data"));
            string original = Path.Combine(directory, "a video\nname.mkv");
            string claim = Path.Combine(directory, ".vdf-private.holding");
            File.WriteAllBytes(claim, [1, 2, 3]);
            Assert.True(FileUtils.MoveToTrash(claim, original));
            Assert.False(File.Exists(claim));
            Assert.True(File.Exists(Path.Combine(directory, "data/Trash/files", Path.GetFileName(original))));
            string metadata = File.ReadAllText(Path.Combine(directory, "data/Trash/info", Path.GetFileName(original) + ".trashinfo"));
            string pathLine = Assert.Single(metadata.Split('\n'), s => s.StartsWith("Path="));
            Assert.Equal(original, Uri.UnescapeDataString(pathLine[5..]));
            Assert.DoesNotContain(".vdf-private", metadata);
        }
        finally {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
            Directory.Delete(directory, true);
        }
    }
}
