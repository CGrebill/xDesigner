using XDesigner.Core.Properties;
using XDesigner.Protocol.Metadata;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace XDesigner.App.Controls;

/// <summary>What editors need from the properties window.</summary>
internal sealed class PropertyEditorContext
{
    /// <summary>Writes a value (null = reset) to the given elements; returns a problem to show, or null.</summary>
    public required Func<IReadOnlyList<int>, string, string?, string?> Commit { get; init; }

    /// <summary>Shows (or with null, clears) a validation message under the grid.</summary>
    public required Action<string?> ReportProblem { get; init; }

    /// <summary>Selects another element (a "Content: button1" link).</summary>
    public required Action<int> SelectElement { get; init; }

    /// <summary>Font names for the FontFamily list; may be empty while loading.</summary>
    public required Func<IReadOnlyList<string>> FontFamilies { get; init; }

    /// <summary>The document's project folder (images live in its Assets folder, §12); null without a project.</summary>
    public required Func<string?> ProjectDirectory { get; init; }

    /// <summary>"Browse…": pick an image file, copy it into Assets, hand its XAML path to the callback, then rebuild.</summary>
    public required Func<Action<string>, Task> BrowseImageAsync { get; init; }
}

/// <summary>
/// The editor in one row of the properties window (§9.2). An editor remembers the elements it was showing when
/// the user started typing, so a value typed for one control is never written to another after the selection moves.
/// </summary>
internal abstract class PropertyEditor(PropertyEditorContext context)
{
    protected PropertyEditorContext Context { get; } = context;

    public abstract FrameworkElement View { get; }

    public PropertyRow Row { get; private set; } = null!;

    /// <summary>The elements a commit writes to.</summary>
    protected IReadOnlyList<int> Targets { get; private set; } = [];

    /// <summary>True while the user has typed something not yet committed.</summary>
    public virtual bool HasPendingEdit => false;

    public void Show(PropertyRow row, IReadOnlyList<int> targets)
    {
        Row = row;
        if (!HasPendingEdit)
        {
            Targets = targets;
            Apply(row);
        }
    }

    /// <summary>Commits a pending edit now (the selection is about to change).</summary>
    public virtual void Flush()
    {
    }

    protected abstract void Apply(PropertyRow row);

    protected string? Commit(string? value)
    {
        var problem = Context.Commit(Targets, Row.Key, value);
        Context.ReportProblem(problem);
        return problem;
    }

    /// <summary>The effective value as shown greyed in an empty box ("Auto" for NaN sizes).</summary>
    protected static string Display(PropertyRow row) => row.IsMixed ? "(different values)" : row.EffectiveValue switch
    {
        null => "",
        "NaN" => "Auto",
        var v => v,
    };

    protected static Button SmallButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }
}

/// <summary>
/// Text box for everything typed: strings, numbers (Up/Down step, Shift ×10), thickness, colors, grid lengths, fonts.
/// Commits on Enter or when focus leaves; Esc reverts. Input is validated before anything is written.
/// </summary>
internal sealed class TextPropertyEditor : PropertyEditor
{
    private readonly Grid _root = new() { ColumnSpacing = 4 };
    private readonly TextBox _box = new() { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    // The text last shown from the document. WinUI raises TextChanged asynchronously (after a programmatic set has
    // returned), so "is the user editing" is decided by comparing with this, never by flags around the setter.
    private string _applied = "";

    public TextPropertyEditor(PropertyEditorContext context, PropertyRow row)
        : base(context)
    {
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(_box);
        AutomationName(row.Key);

        _box.PreviewKeyDown += Box_KeyDown; // preview: a text box handles Shift+Up/Down itself (selection) before KeyDown
        _box.LostFocus += (_, _) => CommitText(_box.Text, revertIfInvalid: true);

        if (Accessory(row) is { } accessory)
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(accessory, 1);
            _root.Children.Add(accessory);
        }
    }

    public override FrameworkElement View => _root;

    public override bool HasPendingEdit => _box.Text != _applied;

    /// <summary>Lets a composite editor (the color editor) host this box.</summary>
    public TextBox Box => _box;

    public override void Flush() => CommitText(_box.Text, revertIfInvalid: true);

    protected override void Apply(PropertyRow row)
    {
        var text = row.IsMixed ? "" : row.LocalValue ?? "";
        _box.Text = text;
        _applied = _box.Text; // as the box stores it

        // A single-line box would drop the other lines if edited: multi-line text is edited with the … button.
        var multiline = text.Contains('\n', StringComparison.Ordinal);
        _box.IsReadOnly = multiline;
        ToolTipService.SetToolTip(_box, multiline ? "Several lines of text: edit with the … button." : null);
        _box.PlaceholderText = Display(row);
        ClearError();
    }

    /// <summary>Sets the text as if typed and commits it (flyouts, number stepping).</summary>
    public void CommitTyped(string text)
    {
        // Committed as given, not read back from the box: a single-line box keeps only the first line of text.
        _box.Text = text;
        CommitText(text, revertIfInvalid: false);
    }

    private void Box_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                CommitText(_box.Text, revertIfInvalid: false);
                e.Handled = true;
                break;
            case VirtualKey.Escape when HasPendingEdit:
                Apply(Row);
                Context.ReportProblem(null);
                e.Handled = true;
                break;
            case VirtualKey.Up or VirtualKey.Down when Row.Kind is ValueKind.Int or ValueKind.Double:
                Step(e.Key == VirtualKey.Up ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    private void Step(int direction)
    {
        var current = XamlValueSerializer.TryParseDouble(_box.Text, out var typed) ? typed
            : Row.EffectiveValue is { } eff && XamlValueSerializer.TryParseDouble(eff, out var effective) ? effective
            : 0;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var step = (Row.Key == "Opacity" ? 0.1 : 1) * (shift ? 10 : 1) * direction;
        var next = Row.Kind == ValueKind.Int ? Math.Round(current + step) : Math.Round(current + step, 4);
        if (Row.Key == "Opacity")
        {
            next = Math.Clamp(next, 0, 1);
        }

        CommitTyped(XamlValueText.Double(next));
        _box.SelectionStart = _box.Text.Length;
    }

    private void CommitText(string text, bool revertIfInvalid)
    {
        if (text == _applied)
        {
            return;
        }

        if (!XamlValueSerializer.TryNormalize(Row.Kind, text, Row.EnumValues, out var normalized, out var problem))
        {
            Context.ReportProblem($"{Row.Key}: {problem}");
            if (revertIfInvalid)
            {
                Apply(Row);
            }
            else
            {
                _box.BorderBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            }

            return;
        }

        _applied = _box.Text; // committed: no longer pending (the refresh shows the normalized text)
        ClearError();

        // Nothing typed into a property that isn't set: leave it alone rather than writing "" or resetting nothing.
        if (!Row.IsSet && (normalized is null || (normalized.Length == 0 && Row.LocalValue is null)))
        {
            Apply(Row);
            return;
        }

        if (Commit(normalized) is not null)
        {
            Apply(Row);
        }
    }

    private void ClearError() => _box.ClearValue(Control.BorderBrushProperty);

    private void AutomationName(string key) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_box, key);

    /// <summary>The "…" button next to the box: multi-line text, the four sides of a thickness, or the font list.</summary>
    private Button? Accessory(PropertyRow row) => row.Kind switch
    {
        ValueKind.String or ValueKind.Object => MultilineButton(),
        ValueKind.Thickness => SidesButton(["Left", "Top", "Right", "Bottom"], (l, t, r, b) => XamlValueText.Thickness(l, t, r, b)),
        ValueKind.CornerRadius => SidesButton(["Top left", "Top right", "Bottom right", "Bottom left"], (a, b, c, d) => XamlValueText.CornerRadius(a, b, c, d)),
        ValueKind.FontFamily => FontButton(),
        ValueKind.ImageSource => ImageButton(),
        _ => null,
    };

    /// <summary>
    /// The image picker (§12): the project's images (with thumbnails), Browse… to add one from anywhere (copied into
    /// Assets, then the project is rebuilt so the renderer has it), and Remove image.
    /// </summary>
    private Button ImageButton()
    {
        var button = SmallButton("", "Choose an image");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Choose an image");
        var list = new ListView { Width = 300, MaxHeight = 320, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 300, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var browse = new Button { Content = "Browse…", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var remove = new Button { Content = "Remove image" };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { list, note, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { browse, remove } } },
            },
        };

        flyout.Opening += (_, _) =>
        {
            list.Items.Clear();
            var dir = Context.ProjectDirectory();
            browse.IsEnabled = dir is not null;
            remove.IsEnabled = Row.IsSet;
            if (dir is null)
            {
                note.Text = "Images are packaged with a project. Open the project this file belongs to (Project › Open project) to add one.";
                list.Visibility = Visibility.Collapsed;
                return;
            }

            var images = Core.Assets.ProjectAssets.ListImages(dir);
            list.Visibility = images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            note.Text = images.Count > 0
                ? "Pick one of the project's images, or Browse… to copy another into its Assets folder."
                : "The project has no images yet. Browse… copies one into its Assets folder.";
            foreach (var image in images)
            {
                list.Items.Add(ImageRow(dir, image));
            }
        };
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: string path })
            {
                flyout.Hide();
                CommitTyped(path);
            }
        };
        browse.Click += async (_, _) =>
        {
            flyout.Hide();
            await Context.BrowseImageAsync(CommitTyped);
        };
        remove.Click += (_, _) =>
        {
            flyout.Hide();
            Commit(null);
        };
        button.Flyout = flyout;
        return button;
    }

    private static StackPanel ImageRow(string projectDirectory, string xamlPath)
    {
        var thumbnail = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform };
        if (Core.Assets.ProjectAssets.ToFilePath(projectDirectory, xamlPath) is { } file)
        {
            try
            {
                thumbnail.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file)) { DecodePixelWidth = 32 };
            }
            catch (UriFormatException)
            {
                // No thumbnail; the name is enough.
            }
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Tag = xamlPath, Children = { thumbnail, new TextBlock { Text = xamlPath, VerticalAlignment = VerticalAlignment.Center } } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, xamlPath);
        return row;
    }

    private Button MultilineButton()
    {
        var button = SmallButton("", "Edit as several lines");
        var editor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 300, Height = 140 };
        var apply = new Button { Content = "Apply", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { editor, apply } } };
        flyout.Opening += (_, _) => editor.Text = HasPendingEdit ? _box.Text : Row.IsMixed ? "" : Row.LocalValue ?? ""; // the box may show only the first line
        apply.Click += (_, _) =>
        {
            flyout.Hide();
            CommitTyped(editor.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        };
        button.Flyout = flyout;
        return button;
    }

    private Button SidesButton(string[] labels, Func<double, double, double, double, string> format)
    {
        var button = SmallButton("", "Edit each side");
        var boxes = labels.Select(l => new TextBox { Header = l, Width = 80 }).ToArray();
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        for (var i = 0; i < boxes.Length; i++)
        {
            Grid.SetColumn(boxes[i], i % 2);
            Grid.SetRow(boxes[i], i / 2);
            grid.Children.Add(boxes[i]);
        }

        var apply = new Button { Content = "Apply", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { grid, apply } } };
        flyout.Opening += (_, _) =>
        {
            var source = _box.Text.Length > 0 ? _box.Text : Row.EffectiveValue ?? "0";
            var values = XamlValueSerializer.TryParseNumbers(source, out var n) ? n switch
            {
                [var u] => [u, u, u, u],
                [var h, var v] => [h, v, h, v],
                [_, _, _, _] => n,
                _ => [0, 0, 0, 0],
            } : new double[] { 0, 0, 0, 0 };
            for (var i = 0; i < 4; i++)
            {
                boxes[i].Text = XamlValueText.Double(values[i]);
            }
        };
        apply.Click += (_, _) =>
        {
            var v = new double[4];
            for (var i = 0; i < 4; i++)
            {
                if (!XamlValueSerializer.TryParseDouble(boxes[i].Text, out v[i]))
                {
                    Context.ReportProblem($"{Row.Key}: '{boxes[i].Text}' isn't a number.");
                    return;
                }
            }

            flyout.Hide();
            CommitTyped(format(v[0], v[1], v[2], v[3]));
        };
        button.Flyout = flyout;
        return button;
    }

    private Button FontButton()
    {
        var button = SmallButton("", "Choose a font");
        var list = new ListView { Width = 260, MaxHeight = 360, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var flyout = new Flyout { Content = list };
        flyout.Opening += (_, _) =>
        {
            var fonts = Context.FontFamilies();
            list.ItemsSource = fonts.Count > 0 ? fonts : new[] { "(no fonts reported by the renderer)" };
        };
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is string font && !font.StartsWith('('))
            {
                flyout.Hide();
                CommitTyped(font);
            }
        };
        button.Flyout = flyout;
        return button;
    }
}

/// <summary>True/False check box (a mixed selection shows the indeterminate state).</summary>
internal sealed class BoolPropertyEditor : PropertyEditor
{
    private readonly CheckBox _box = new() { MinWidth = 0, Content = null };
    private bool _setting;

    public BoolPropertyEditor(PropertyEditorContext context, PropertyRow row)
        : base(context)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_box, row.Key);
        _box.Click += (_, _) =>
        {
            if (!_setting)
            {
                Commit(_box.IsChecked == true ? "True" : "False");
            }
        };
    }

    public override FrameworkElement View => _box;

    protected override void Apply(PropertyRow row)
    {
        _setting = true;
        _box.IsChecked = row.IsMixed ? null : string.Equals(row.LocalValue ?? row.EffectiveValue, "True", StringComparison.OrdinalIgnoreCase);
        _box.Opacity = row.IsSet ? 1 : 0.75;
        _setting = false;
    }
}

/// <summary>Drop-down of an enum's names; the effective value shows greyed when nothing is set.</summary>
internal sealed class EnumPropertyEditor : PropertyEditor
{
    private readonly Grid _root = new();
    private readonly ComboBox _box = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };

    // ComboBox.PlaceholderText doesn't reappear once the selection is cleared again (WinUI), so the greyed
    // effective value is drawn on top of the empty box instead.
    private readonly TextBlock _placeholder = new()
    {
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 32, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private bool _setting;

    public EnumPropertyEditor(PropertyEditorContext context, PropertyRow row)
        : base(context)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_box, row.Key);
        _placeholder.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        _root.Children.Add(_box);
        _root.Children.Add(_placeholder);
        _box.ItemsSource = row.EnumValues ?? [];
        _box.SelectionChanged += (_, _) =>
        {
            _placeholder.Visibility = _box.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;
            if (!_setting && _box.SelectedItem is string value && value != Row.LocalValue)
            {
                Commit(value);
            }
        };
    }

    public override FrameworkElement View => _root;

    protected override void Apply(PropertyRow row)
    {
        _setting = true;
        _box.SelectedItem = !row.IsMixed && row.LocalValue is { } local && row.EnumValues?.Contains(local) == true ? local : null;
        _placeholder.Text = Display(row);
        _placeholder.Visibility = _box.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;
        _setting = false;
    }
}

/// <summary>Color swatch (opens a color picker and a palette) next to a text box for names and #hex.</summary>
internal sealed class ColorPropertyEditor : PropertyEditor
{
    private readonly Grid _root = new() { ColumnSpacing = 4 };
    private readonly TextPropertyEditor _text;
    private readonly Border _swatch = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    private readonly ColorPicker _picker = new()
    {
        IsAlphaEnabled = true,
        IsMoreButtonVisible = false,
        IsColorSliderVisible = true,
        IsColorChannelTextInputVisible = false,
        IsHexInputVisible = true,
        ColorSpectrumShape = ColorSpectrumShape.Ring,
    };

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _pickerChanged;
    private Color _openedColor;

    public ColorPropertyEditor(PropertyEditorContext context, PropertyRow row)
        : base(context)
    {
        _text = new TextPropertyEditor(context, row);
        _swatch.BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];

        var button = new Button { Content = _swatch, Padding = new Thickness(5), MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTipService.SetToolTip(button, "Pick a color");
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_text.View, 1);
        _root.Children.Add(button);
        _root.Children.Add(_text.View);

        var palette = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxWidth = 320 };
        foreach (var (name, rgb) in XamlValueSerializer.Palette)
        {
            var chip = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                Background = new SolidColorBrush(name == "Transparent" ? Colors.Transparent : FromRgb(rgb)),
                Tag = name,
            };
            ToolTipService.SetToolTip(chip, name);
            palette.Items.Add(chip);
        }

        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { _picker, new TextBlock { Text = "Named colors", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] }, palette } } };
        flyout.Opening += (_, _) =>
        {
            _openedColor = CurrentColor(Row) ?? Colors.White;
            _picker.Color = _openedColor;
            _pickerChanged = false;
        };
        flyout.Closed += (_, _) =>
        {
            if (_debounce.IsEnabled)
            {
                _debounce.Stop();
                CommitPicker();
            }
        };
        _picker.ColorChanged += (_, e) =>
        {
            // Raised for the programmatic set on opening too (possibly later): only a different color is a user change.
            if (e.NewColor != _openedColor)
            {
                _pickerChanged = true;
                _debounce.Stop();
                _debounce.Start(); // live preview while dragging; one undo step (same property, merged)
            }
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            CommitPicker();
        };
        palette.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is Border { Tag: string name })
            {
                flyout.Hide();
                _text.CommitTyped(name);
            }
        };
        button.Flyout = flyout;
    }

    public override FrameworkElement View => _root;

    public override bool HasPendingEdit => _text.HasPendingEdit;

    public override void Flush() => _text.Flush();

    protected override void Apply(PropertyRow row)
    {
        _text.Show(row, Targets);
        var color = CurrentColor(row);
        _swatch.Background = color is { } c ? new SolidColorBrush(c) : null;
    }

    private void CommitPicker()
    {
        if (_pickerChanged)
        {
            _pickerChanged = false;
            var c = _picker.Color;
            _text.CommitTyped(XamlValueText.Color(c.A, c.R, c.G, c.B));
        }
    }

    /// <summary>The color written (hex or palette name), else the effective one from the renderer.</summary>
    private static Color? CurrentColor(PropertyRow row)
    {
        foreach (var text in new[] { row.IsMixed ? null : row.LocalValue, row.EffectiveValue })
        {
            if (text is null)
            {
                continue;
            }

            if (XamlValueSerializer.TryParseColor(text, out var argb, out var name))
            {
                if (argb is { } v)
                {
                    return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
                }

                if (XamlValueSerializer.Palette.FirstOrDefault(p => p.Name == name) is { Name: not null } known)
                {
                    return name == "Transparent" ? Colors.Transparent : FromRgb(known.Rgb);
                }
            }
        }

        return null;
    }

    private static Color FromRgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>
/// Values the window shows but doesn't edit: bindings and resources (D7), property elements, unsupported types, and
/// child controls (a link that selects the child). Reset still works for them.
/// </summary>
internal sealed class ReadOnlyPropertyEditor : PropertyEditor
{
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };

    public ReadOnlyPropertyEditor(PropertyEditorContext context)
        : base(context)
    {
    }

    public override FrameworkElement View => _host;

    protected override void Apply(PropertyRow row)
    {
        if (row.Form == PropertyValueForm.ChildElements && row.LinkedElementId is { } child)
        {
            var link = new HyperlinkButton { Content = row.Note, Padding = new Thickness(4, 2, 4, 2) };
            ToolTipService.SetToolTip(link, "Select this control");
            link.Click += (_, _) => Context.SelectElement(child);
            _host.Content = link;
            return;
        }

        var markup = row.Form == PropertyValueForm.Markup;
        var text = new TextBlock
        {
            Text = row.Note ?? (row.IsMixed ? "(different values)" : row.LocalValue ?? Display(row)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[row.IsSet ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush"],
            FontStyle = row.Form == PropertyValueForm.PropertyElement ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        };
        ToolTipService.SetToolTip(text, markup ? $"{text.Text}\nBindings and resources are kept as written; Reset removes it." : text.Text);
        _host.Content = markup
            ? new Border
            {
                Background = (Brush)Application.Current.Resources["AccentFillColorTertiaryBrush"],
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock { Text = text.Text, Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"], TextTrimming = TextTrimming.CharacterEllipsis },
            }
            : text;
    }
}
