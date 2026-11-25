using System.Xml.Linq;
using XDesigner.Core.Editing;
using XDesigner.Core.Xaml;
using Microsoft.Extensions.Time.Testing;

namespace XDesigner.Core.Tests;

public class UndoTests
{
    private static readonly XNamespace Av = Frameworks.FrameworkProfile.AvaloniaNamespace;

    private const string Source =
        "<Window xmlns=\"https://github.com/avaloniaui\"\r\n" +
        "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\r\n" +
        "        x:Class=\"App.Main\" Title=\"Hi\">\r\n" +
        "  <Grid RowDefinitions=\"Auto,*\">\r\n" +
        "    <TextBlock Text=\"One\" />\r\n" +
        "    <Button x:Name=\"ok\" Content=\"OK\" Grid.Row=\"1\" />\r\n" +
        "    <Border></Border>\r\n" +
        "    <StackPanel />\r\n" +
        "    <Canvas>\r\n" +
        "    </Canvas>\r\n" +
        "  </Grid>\r\n" +
        "</Window>\r\n";

    private static (XamlDocument Doc, UndoManager Undo) Open() =>
        Open(new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    private static (XamlDocument Doc, UndoManager Undo) Open(TimeProvider clock)
    {
        var doc = XamlDocument.Parse(Source);
        return (doc, new UndoManager(doc, clock));
    }

    private static int Id(XamlDocument doc, string type) => doc.AllNodes.First(n => n.TypeName == type).Id;

    private static XElement NewButton() => new(Av + "Button", new XAttribute("Content", "New"));

    public static TheoryData<string> Containers() => ["Grid", "Border", "StackPanel", "Canvas", "Window"];

    [Fact]
    public void TrimmingPastTheOpenedState_KeepsTheDocumentDirty()
    {
        var (doc, undo) = Open();
        var stack = Id(doc, "StackPanel");

        for (var i = 0; i <= UndoManager.MaxDepth; i++)
        {
            undo.Execute(new InsertElementCommand(stack, NewButton(), int.MaxValue, "Add"));
        }

        while (undo.CanUndo)
        {
            undo.Undo();
        }

        Assert.NotEqual(Source, doc.ToXaml()); // the first edit can no longer be undone…
        Assert.True(undo.IsDirty);             // …so the document still differs from the file
    }

    [Theory]
    [MemberData(nameof(Containers))]
    public void InsertThenUndo_RestoresExactText(string container)
    {
        var (doc, undo) = Open();

        undo.Execute(new InsertElementCommand(Id(doc, container), NewButton(), int.MaxValue, "Add Button"));
        Assert.NotEqual(Source, doc.ToXaml());
        undo.Undo();

        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void Insert_IndentsLikeSiblings()
    {
        var (doc, undo) = Open();

        undo.Execute(new InsertElementCommand(Id(doc, "Grid"), NewButton(), 1, "Add"));

        Assert.Contains("    <TextBlock Text=\"One\" />\r\n    <Button Content=\"New\" />\r\n    <Button x:Name=\"ok\"", doc.ToXaml());
    }

    [Fact]
    public void InsertIntoEmptyContainers_IndentsOneLevelDeeper()
    {
        var (doc, undo) = Open();

        undo.Execute(new InsertElementCommand(Id(doc, "StackPanel"), NewButton(), 0, "Add"));
        undo.Execute(new InsertElementCommand(Id(doc, "Border"), NewButton(), 0, "Add"));
        undo.Execute(new InsertElementCommand(Id(doc, "Canvas"), NewButton(), 0, "Add"));

        var text = doc.ToXaml();
        Assert.Contains("    <StackPanel>\r\n      <Button Content=\"New\" />\r\n    </StackPanel>\r\n", text);
        Assert.Contains("    <Border>\r\n      <Button Content=\"New\" />\r\n    </Border>\r\n", text);
        Assert.Contains("    <Canvas>\r\n      <Button Content=\"New\" />\r\n    </Canvas>\r\n", text);
    }

    [Fact]
    public void SetAndRemoveAttributes_UndoExactly()
    {
        var (doc, undo) = Open();
        var button = Id(doc, "Button");

        undo.Execute(new SetAttributeCommand(button, "Content", "Changed"));
        undo.Execute(new SetAttributeCommand(button, "Content", null));          // remove a middle attribute
        undo.Execute(new SetAttributeCommand(doc.Root.Id, XName.Get("Class", Frameworks.FrameworkProfile.Xaml2006Namespace), null));
        undo.Execute(new SetAttributeCommand(doc.Root.Id, XNamespace.Xmlns + "local", "using:App"));

        undo.Undo();
        undo.Undo();
        undo.Undo();
        undo.Undo();

        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void RemoveElement_UndoRestoresPositionAndWhitespace()
    {
        var (doc, undo) = Open();

        undo.Execute(new RemoveElementCommand(Id(doc, "Button")));
        undo.Execute(new RemoveElementCommand(Id(doc, "TextBlock")));

        Assert.DoesNotContain("TextBlock", doc.ToXaml());
        Assert.Contains("<Grid RowDefinitions=\"Auto,*\">\r\n    <Border>", doc.ToXaml());

        undo.Undo();
        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void Redo_ReappliesAndKeepsIds()
    {
        var (doc, undo) = Open();
        var element = NewButton();
        undo.Execute(new InsertElementCommand(Id(doc, "StackPanel"), element, 0, "Add"));
        var afterInsert = doc.ToXaml();
        var id = doc.NodeFor(element).Id;

        undo.Undo();
        Assert.Null(doc.FindById(id));
        undo.Redo();

        Assert.Equal(afterInsert, doc.ToXaml());
        Assert.Equal(id, doc.FindById(id)!.Id);
    }

    [Fact]
    public void Composite_IsOneStep_AndUndoesInReverse()
    {
        var (doc, undo) = Open();
        var element = NewButton();
        var changes = 0;
        doc.Changed += (_, _) => changes++;

        undo.Execute(new CompositeCommand("Add Button", [
            new SetAttributeCommand(doc.Root.Id, XNamespace.Xmlns + "local", "using:App"),
            new InsertElementCommand(Id(doc, "StackPanel"), element, 0, "Add"),
        ]));
        var newId = doc.NodeFor(element).Id;
        undo.Execute(new SetAttributeCommand(newId, "Width", "80"));

        Assert.Equal(2, changes); // one re-render per action
        Assert.Equal("Set Width", undo.UndoDescription);
        undo.Undo();
        Assert.Equal("Add Button", undo.UndoDescription);
        undo.Undo();
        Assert.False(undo.CanUndo);
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void SamePropertyEditsWithinOneSecond_Merge()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (doc, undo) = Open(clock);
        var button = Id(doc, "Button");

        undo.Execute(new SetAttributeCommand(button, "Content", "O"));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        undo.Execute(new SetAttributeCommand(button, "Content", "OK!"));
        clock.Advance(TimeSpan.FromSeconds(2));
        undo.Execute(new SetAttributeCommand(button, "Content", "Later"));

        undo.Undo();
        Assert.Contains("Content=\"OK!\"", doc.ToXaml());
        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void Dirty_TracksSavePoint()
    {
        var (doc, undo) = Open();
        Assert.False(undo.IsDirty);

        undo.Execute(new SetAttributeCommand(doc.Root.Id, "Title", "A"));
        Assert.True(undo.IsDirty);
        undo.MarkSaved();
        Assert.False(undo.IsDirty);

        undo.Undo();
        Assert.True(undo.IsDirty);   // back before the save
        undo.Redo();
        Assert.False(undo.IsDirty);  // at the save again
    }

    [Fact]
    public void MergingIntoTheSavedStep_MakesDocumentDirty()
    {
        var (doc, undo) = Open();
        undo.Execute(new SetAttributeCommand(doc.Root.Id, "Title", "A"));
        undo.MarkSaved();

        // MarkSaved starts a fresh step, so this must not merge silently into the saved one.
        undo.Execute(new SetAttributeCommand(doc.Root.Id, "Title", "AB"));

        Assert.True(undo.IsDirty);
        undo.Undo();
        Assert.False(undo.IsDirty);
    }
}

public class EmptiedContainerTests
{
    [Fact]
    public void RemovingTheLastChild_RestoresTheOriginalEmptyTag_AndUndoIsExact()
    {
        const string source = "<Grid xmlns=\"https://github.com/avaloniaui\">\n  <Canvas Background=\"Yellow\" />\n</Grid>\n";
        var doc = XamlDocument.Parse(source);
        var undo = new UndoManager(doc);
        var canvas = doc.AllNodes.Single(n => n.TypeName == "Canvas");

        undo.Execute(new InsertElementCommand(canvas.Id, new XElement((XNamespace)Frameworks.FrameworkProfile.AvaloniaNamespace + "Button"), 0, "Add"));
        var button = doc.AllNodes.Single(n => n.TypeName == "Button");
        undo.Execute(new RemoveElementCommand(button.Id));

        Assert.Equal(source, doc.ToXaml()); // "<Canvas ... />" again, no leftover blank line
        undo.Undo();
        Assert.Contains("<Canvas Background=\"Yellow\">\n    <Button />\n  </Canvas>", doc.ToXaml());
        undo.Undo();
        Assert.Equal(source, doc.ToXaml());
    }
}
