using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

public class ProjectFoldersTests
{
    private static ProjectsData Sample()
    {
        var d = new ProjectsData();
        d.Projects.Add(new CopitProject { Name = "Shop API", Group = "Kunde A/Backend", LastOpened = DateTime.Now.AddDays(-1) });
        d.Projects.Add(new CopitProject { Name = "Shop Web", Group = "Kunde A", LastOpened = DateTime.Now });
        d.Projects.Add(new CopitProject { Name = "Intern", Group = "" });
        d.Folders.Add(new ProjectFolder { Path = "Leer" });
        return d;
    }

    [Fact]
    public void FlattensFoldersFirstWithDepthAndCounts()
    {
        var rows = ProjectFolders.Flatten(Sample());
        Assert.Equal(["Kunde A", "Backend", "Shop API", "Shop Web", "Leer", "Intern"], rows.Select(r => r.Name));
        Assert.Equal([0, 1, 2, 1, 0, 0], rows.Select(r => r.Depth));
        Assert.Equal(2, rows[0].Count);
        Assert.Equal(0, rows.Single(r => r.Name == "Leer").Count);
    }

    [Fact]
    public void CollapsedFolderHidesContent()
    {
        var d = Sample();
        Assert.True(ProjectFolders.Toggle(d, "Kunde A"));
        Assert.Equal(["Kunde A", "Leer", "Intern"], ProjectFolders.Flatten(d).Select(r => r.Name));
        Assert.False(ProjectFolders.Toggle(d, "Kunde A"));
    }

    [Fact]
    public void RenameMovesSubfoldersAndProjects()
    {
        var d = Sample();
        ProjectFolders.Toggle(d, "Kunde A/Backend");
        ProjectFolders.Rename(d, "Kunde A", "Kunden/A");
        Assert.Equal("Kunden/A/Backend", d.Projects[0].Group);
        Assert.Equal("Kunden/A", d.Projects[1].Group);
        Assert.True(ProjectFolders.IsCollapsed(d, "Kunden/A/Backend"));
        Assert.Throws<InvalidOperationException>(() => ProjectFolders.Rename(d, "Kunden", "Kunden/A/X"));
    }

    [Fact]
    public void DeleteMovesContentOneLevelUp()
    {
        var d = Sample();
        ProjectFolders.Delete(d, "Kunde A");
        Assert.Equal("Backend", d.Projects[0].Group);
        Assert.Equal("", d.Projects[1].Group);
        Assert.Contains("Backend", ProjectFolders.AllPaths(d));
        ProjectFolders.Delete(d, "Leer");
        Assert.DoesNotContain("Leer", ProjectFolders.AllPaths(d));
    }

    [Fact]
    public void MoveNormalizesPath()
    {
        var d = Sample();
        ProjectFolders.Move(d, d.Projects[2], @" Neu \ Sub ");
        Assert.Equal("Neu/Sub", d.Projects[2].Group);
        Assert.Contains("Neu", ProjectFolders.AllPaths(d));
    }
}
