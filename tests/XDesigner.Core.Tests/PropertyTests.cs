using XDesigner.Core.Editing;
using XDesigner.Core.Metadata;
using XDesigner.Core.Properties;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using Microsoft.Extensions.Time.Testing;

namespace XDesigner.Core.Tests;

public class XamlValueSerializerTests
{
    [Theory]
    [InlineData(ValueKind.Thickness, "8", "8")]
    [InlineData(ValueKind.Thickness, "8 4", "8,4")]
    [InlineData(ValueKind.Thickness, "8,4,8,4", "8,4")]
    [InlineData(ValueKind.Thickness, " 1, 2,3 ,4 ", "1,2,3,4")]
    [InlineData(ValueKind.CornerRadius, "4", "4")]
    [InlineData(ValueKind.CornerRadius, "4,4,0,0", "4,4,0,0")]
    [InlineData(ValueKind.Brush, "#f00", "#FF0000")]
    [InlineData(ValueKind.Brush, "#80ff0000", "#80FF0000")]
    [InlineData(ValueKind.Brush, "cornflowerblue", "CornflowerBlue")]
    [InlineData(ValueKind.Color, "#FFFFFFFF", "#FFFFFF")]
    [InlineData(ValueKind.Double, "1.50", "1.5")]
    [InlineData(ValueKind.Double, "Auto", null)]
    [InlineData(ValueKind.Double, "", null)]
    [InlineData(ValueKind.Int, "3", "3")]
    [InlineData(ValueKind.Bool, "true", "True")]
    [InlineData(ValueKind.GridLength, "2*", "2*")]
    [InlineData(ValueKind.GridLength, "auto", "Auto")]
    [InlineData(ValueKind.String, "  spaced  ", "  spaced  ")]
    [InlineData(ValueKind.String, "", "")]
    public void Normalizes(ValueKind kind, string input, string? expected)
    {
        Assert.True(XamlValueSerializer.TryNormalize(kind, input, null, out var normalized, out var problem), problem);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(ValueKind.Thickness, "1,2,3")]
    [InlineData(ValueKind.Thickness, "a")]
    [InlineData(ValueKind.CornerRadius, "1,2")]
    [InlineData(ValueKind.Brush, "notacolor")]
    [InlineData(ValueKind.Brush, "#12345")]
    [InlineData(ValueKind.Double, "12px")]
    [InlineData(ValueKind.Int, "1.5")]
    [InlineData(ValueKind.Int, "3000000000")]
    [InlineData(ValueKind.Thickness, "NaN")]
    [InlineData(ValueKind.Thickness, "1,Infinity,1,1")]
    [InlineData(ValueKind.CornerRadius, "1e400")]
    [InlineData(ValueKind.GridLength, "-5")]
    [InlineData(ValueKind.GridLength, "-1*")]
    [InlineData(ValueKind.GridLength, "0*")]
    [InlineData(ValueKind.Unsupported, "x")]
    public void Rejects(ValueKind kind, string input)
    {
        Assert.False(XamlValueSerializer.TryNormalize(kind, input, null, out _, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Enum_MatchesCaseInsensitively_AndRejectsUnknownNames()
    {
        string[] values = ["Left", "Center", "Right", "Stretch"];
        Assert.True(XamlValueSerializer.TryNormalize(ValueKind.Enum, "center", values, out var v, out _));
        Assert.Equal("Center", v);
        Assert.False(XamlValueSerializer.TryNormalize(ValueKind.Enum, "Middle", values, out _, out _));
    }

    [Fact]
    public void Braces_AreEscapedSoTextIsNeverAMarkupExtension()
    {
        Assert.Equal("{}{hello}", XamlValueSerializer.ToAttribute("{hello}"));
        Assert.Equal("{hello}", XamlValueSerializer.FromAttribute("{}{hello}"));
        Assert.True(XamlValueSerializer.IsMarkupExtension("{Binding Name}"));
        Assert.False(XamlValueSerializer.IsMarkupExtension("{}{Binding Name}"));
    }
}

public class PropertyGridTests
{
    private const string Av = ControlTypeInfo.AvaloniaXmlNamespace;

    private static ControlTypeInfo T(string name, ContentModel content, string? contentProperty = null, params string[] bases) =>
        new($"Avalonia.Controls.{name}", name, Av, "Avalonia.Controls", "Avalonia.Controls", true, true, content, contentProperty, null, bases,
            name == "Button" ? ["Click", "PointerPressed"] : null);

    private static readonly ControlCatalog Catalog = new([
        T("Window", ContentModel.SingleContent, "Content"), T("Grid", ContentModel.Children, "Children", "Avalonia.Controls.Panel"),
        T("Canvas", ContentModel.Children, "Children", "Avalonia.Controls.Panel"), T("Button", ContentModel.SingleContent, "Content"),
        T("TextBlock", ContentModel.Inlines, "Inlines"), T("Border", ContentModel.SingleChild, "Child"),
    ]);

    private static PropertyInfoDto P(string name, ValueKind kind, string owner = "Control", bool attached = false) =>
        new(name, owner, "System.Object", kind, attached, true, kind == ValueKind.Enum ? ["Left", "Center", "Right", "Stretch"] : null);

    private static readonly PropertyInfoDto[] ControlProps =
    [
        P("Name", ValueKind.String), P("Width", ValueKind.Double), P("Margin", ValueKind.Thickness), P("Background", ValueKind.Brush),
        P("HorizontalAlignment", ValueKind.Enum), P("Content", ValueKind.Object), P("Text", ValueKind.String), P("Child", ValueKind.Unsupported),
        P("Template", ValueKind.Unsupported), P("Row", ValueKind.Int, "Grid", true), P("Left", ValueKind.Double, "Canvas", true),
        P("Tip", ValueKind.Object, "ToolTip", true),
    ];

    private const string Source = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="T">
          <Grid>
            <Button x:Name="ok" Content="OK" Grid.Row="1" Width="80" Click="ok_Click" Foo.Bar="1" />
            <Button x:Name="cancel" Content="{Binding CancelText}" Width="80">
              <Button.Background>
                <SolidColorBrush Color="Red" />
              </Button.Background>
            </Button>
            <Button x:Name="plain">Plain text</Button>
            <Button x:Name="nested"><TextBlock x:Name="inner" Text="In" /></Button>
            <TextBlock x:Name="label">Hello</TextBlock>
            <Canvas><Border x:Name="box" Canvas.Left="5" /></Canvas>
          </Grid>
        </Window>
        """;

    private static (XamlDocument Doc, UndoManager Undo, DesignerActions Actions) Open(string source = Source)
    {
        var doc = XamlDocument.Parse(source);
        var undo = new UndoManager(doc, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        return (doc, undo, new DesignerActions(doc, undo));
    }

    private static int Id(XamlDocument doc, string name) => doc.AllNodes.Single(n => n.Name == name).Id;

    private static Dictionary<string, PropertyRow> Rows(XamlDocument doc, params string[] names) =>
        PropertyGridBuilder.Build(doc, Catalog, names.Select(n => Id(doc, n)).ToList(), _ => ControlProps, _ => null)
            .ToDictionary(r => r.Key);

    [Fact]
    public void SingleElement_ShowsLocalValuesAndForms()
    {
        var (doc, _, _) = Open();
        var ok = Rows(doc, "ok");

        Assert.Equal((PropertyValueForm.Attribute, "OK"), (ok["Content"].Form, ok["Content"].LocalValue));
        Assert.Equal("1", ok["Grid.Row"].LocalValue);
        Assert.Equal(PropertyCategories.Layout, ok["Grid.Row"].Category);
        Assert.False(ok["Margin"].IsSet);
        Assert.False(ok.ContainsKey("Name"));         // edited in the header
        Assert.False(ok.ContainsKey("Canvas.Left"));  // the parent is a Grid
        Assert.False(ok.ContainsKey("Click"));        // an event, not a property
        Assert.True(ok["Template"].IsAdvanced);
        Assert.Equal((PropertyCategories.Other, true), (ok["Foo.Bar"].Category, ok["Foo.Bar"].IsReadOnly));

        var cancel = Rows(doc, "cancel");
        Assert.Equal(PropertyValueForm.Markup, cancel["Content"].Form);
        Assert.Equal("{Binding CancelText}", cancel["Content"].Note);
        Assert.True(cancel["Content"].IsReadOnly);
        Assert.Equal(PropertyValueForm.PropertyElement, cancel["Background"].Form);

        Assert.Equal((PropertyValueForm.TextContent, "Plain text"), (Rows(doc, "plain")["Content"].Form, Rows(doc, "plain")["Content"].LocalValue));
        Assert.Equal((PropertyValueForm.TextContent, "Hello"), (Rows(doc, "label")["Text"].Form, Rows(doc, "label")["Text"].LocalValue));

        var nested = Rows(doc, "nested")["Content"];
        Assert.Equal(PropertyValueForm.ChildElements, nested.Form);
        Assert.Equal(Id(doc, "inner"), nested.LinkedElementId);

        var box = Rows(doc, "box");
        Assert.Equal("5", box["Canvas.Left"].LocalValue);
        Assert.False(box.ContainsKey("Grid.Row"));
    }

    [Fact]
    public void Categories_AreOrdered()
    {
        var (doc, _, _) = Open();
        var rows = PropertyGridBuilder.Build(doc, Catalog, [Id(doc, "ok")], _ => ControlProps, _ => null);

        var categories = rows.Select(r => r.Category).Distinct().ToList();
        Assert.Equal(PropertyCategories.Common, categories[0]);
        Assert.Equal(PropertyCategories.Other, categories[^1]);
        Assert.True(rows.ToList().FindIndex(r => r.Key == "Width") < rows.ToList().FindIndex(r => r.Key == "Margin"));
    }

    [Fact]
    public void EffectiveValues_ComeFromTheRenderer()
    {
        var (doc, _, _) = Open();
        var id = Id(doc, "ok");
        var values = new Dictionary<string, PropertyValueDto> { ["Margin"] = new("Margin", "0", ValueSource.Default), ["Background"] = new("Background", "#FF333333", ValueSource.Style) };

        var rows = PropertyGridBuilder.Build(doc, Catalog, [id], _ => ControlProps, i => i == id ? values : null).ToDictionary(r => r.Key);

        Assert.Equal(("#FF333333", ValueSource.Style), (rows["Background"].EffectiveValue, rows["Background"].EffectiveSource));
        Assert.Equal(PropertyValueForm.NotSet, rows["Background"].Form);
    }

    [Fact]
    public void MultiSelect_ShowsCommonPropertiesAndMixedValues()
    {
        var (doc, _, _) = Open();
        var rows = Rows(doc, "ok", "cancel", "label");

        Assert.Equal("80", Rows(doc, "ok", "cancel")["Width"].LocalValue);  // same on both
        Assert.True(rows["Width"].IsMixed);                                    // label has no Width
        Assert.Null(rows["Width"].LocalValue);
        Assert.False(rows["Width"].IsReadOnly);
        Assert.True(rows["Content"].IsReadOnly);                                // a binding on one of them
        Assert.False(rows.ContainsKey("Foo.Bar"));                              // unknown attributes only for one element
    }

    [Fact]
    public void SetProperty_WritesAttribute_AndUndoIsExact()
    {
        var (doc, undo, actions) = Open();
        var ok = Id(doc, "ok");

        Assert.True(actions.SetProperty([ok], "Margin", "8,4", Catalog));
        Assert.Contains("Width=\"80\" Click=\"ok_Click\" Foo.Bar=\"1\" Margin=\"8,4\" />", doc.ToXaml());
        Assert.Equal("Set Margin", undo.UndoDescription);

        Assert.False(actions.SetProperty([ok], "Margin", "8,4", Catalog)); // unchanged: no new step
        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void Reset_RemovesAttributePropertyElementAndText()
    {
        var (doc, undo, actions) = Open();

        actions.SetProperty([Id(doc, "cancel")], "Background", null, Catalog);
        actions.SetProperty([Id(doc, "ok")], "Grid.Row", null, Catalog);
        actions.SetProperty([Id(doc, "plain")], "Content", null, Catalog);

        var text = doc.ToXaml();
        Assert.DoesNotContain("Button.Background", text);
        Assert.Contains("<Button x:Name=\"cancel\" Content=\"{Binding CancelText}\" Width=\"80\"></Button>", text);
        Assert.DoesNotContain("Grid.Row", text);
        Assert.Contains("<Button x:Name=\"plain\"></Button>", text);

        undo.Undo();
        undo.Undo();
        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void TextContent_IsEditedInPlace()
    {
        var (doc, undo, actions) = Open();

        actions.SetProperty([Id(doc, "plain")], "Content", "Changed", Catalog);
        actions.SetProperty([Id(doc, "label")], "Text", "World", Catalog);

        Assert.Contains("<Button x:Name=\"plain\">Changed</Button>", doc.ToXaml());
        Assert.Contains("<TextBlock x:Name=\"label\">World</TextBlock>", doc.ToXaml());
        undo.Undo();
        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void SimpleValue_ReplacesAPropertyElement()
    {
        var (doc, undo, actions) = Open();

        actions.SetProperty([Id(doc, "cancel")], "Background", "Blue", Catalog);

        Assert.DoesNotContain("SolidColorBrush", doc.ToXaml());
        Assert.Contains("Background=\"Blue\"", doc.ToXaml());
        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void MultiSelect_EditIsOneStep_AndTypingMerges()
    {
        var (doc, undo, actions) = Open();
        int[] ids = [Id(doc, "ok"), Id(doc, "cancel"), Id(doc, "label")];

        actions.SetProperty(ids, "HorizontalAlignment", "Left", Catalog);
        actions.SetProperty(ids, "HorizontalAlignment", "Center", Catalog);

        Assert.Equal(3, doc.ToXaml().Split("HorizontalAlignment=\"Center\"").Length - 1);
        Assert.Equal("Set HorizontalAlignment on 3 controls", undo.UndoDescription);
        undo.Undo();
        Assert.False(undo.CanUndo);
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void LeadingBrace_IsEscaped()
    {
        var (doc, _, actions) = Open();
        actions.SetProperty([Id(doc, "ok")], "Content", "{not a binding}", Catalog);

        Assert.Contains("Content=\"{}{not a binding}\"", doc.ToXaml());
        Assert.Equal("{not a binding}", Rows(doc, "ok")["Content"].LocalValue);
        Assert.Equal(PropertyValueForm.Attribute, Rows(doc, "ok")["Content"].Form);
    }

    [Fact]
    public void Rename_ValidatesAndKeepsTheExistingAttribute()
    {
        var (doc, undo, actions) = Open();
        var ok = Id(doc, "ok");

        Assert.NotNull(actions.Rename(ok, "cancel"));     // taken
        Assert.NotNull(actions.Rename(ok, "class"));      // keyword
        Assert.NotNull(actions.Rename(ok, "1st"));        // not an identifier
        Assert.Null(actions.Rename(ok, "okButton"));
        Assert.Contains("<Button x:Name=\"okButton\" Content=\"OK\"", doc.ToXaml());
        Assert.Equal("Rename ok to okButton", undo.UndoDescription);

        undo.Undo();
        Assert.Equal(Source, doc.ToXaml());
    }

    [Fact]
    public void Rename_AnUnnamedElement_PutsXNameFirst_AndEmptyRemovesIt()
    {
        const string source = "<Grid xmlns=\"https://github.com/avaloniaui\">\n  <Button Content=\"A\" Width=\"80\" />\n</Grid>\n";
        var (doc, undo, actions) = Open(source);
        var button = doc.AllNodes.Single(n => n.TypeName == "Button").Id;

        Assert.Null(actions.Rename(button, "go"));
        Assert.Contains("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"", doc.ToXaml());
        Assert.Contains("<Button x:Name=\"go\" Content=\"A\" Width=\"80\" />", doc.ToXaml());

        Assert.Null(actions.Rename(button, ""));
        Assert.Contains("<Button Content=\"A\" Width=\"80\" />", doc.ToXaml());

        undo.Undo();
        undo.Undo();
        Assert.Equal(source, doc.ToXaml());
    }
}
