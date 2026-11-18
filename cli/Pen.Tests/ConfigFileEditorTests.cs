namespace Pen.Tests;

public class ConfigFileEditorTests
{
    [Fact]
    public void AddToListCreatesTheProjectStub_AndKeepsAnExistingFilesMode()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "config.toml");
        Assert.Equal(["example.com", ".foo.io"], ConfigFileEditor.AddToList(new(path, ".pen/config.toml", ConfigKind.Project, ProjectTemplate.Render(ProjectDetection.None)), ConfigListKey.Allow, ["example.com", ".foo.io"], RecipeCatalog.Embedded));
        var text = File.ReadAllText(path);
        Assert.StartsWith(ProjectTemplate.Render(ProjectDetection.None), text);
        Assert.Equal(["example.com", ".foo.io"], ConfigParser.ParseProject(text, "x", RecipeCatalog.Embedded).Allow);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(path));

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(["b.io"], ConfigFileEditor.AddToList(new(path, "x", ConfigKind.Project, ""), ConfigListKey.Allow, ["b.io", "example.com"], RecipeCatalog.Embedded));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(["example.com", ".foo.io", "b.io"], ConfigLoader.LoadProject(t.Path, ".pen", RecipeCatalog.Embedded).Allow);
        Assert.Single(Directory.GetFiles(t.Path)); // no temp file left
    }

    [Fact]
    public void AddToListLeavesABrokenFileAlone()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "config.toml");
        File.WriteAllText(path, "allw = 1\n");
        Assert.Throws<PenException>(() => ConfigFileEditor.AddToList(new(path, ".pen/config.toml", ConfigKind.Project, ProjectTemplate.Render(ProjectDetection.None)), ConfigListKey.Allow, ["b.io"], RecipeCatalog.Embedded));
        Assert.Equal("allw = 1\n", File.ReadAllText(path));
    }
}
