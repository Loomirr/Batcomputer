using System.Text;

namespace Batcomputer;

/// <summary>A resizable, read-only native-part inspector with an offline 3D preview and the exact
/// indexed donor/material recipe. Applying the part remains an explicit separate action.</summary>
public sealed class PartInspectorForm : AdaptiveForm
{
    private static readonly HashSet<string> RecipeHeadings = new(StringComparer.Ordinal)
    {
        "Mesh",
        "Source Blueprint",
        "Component",
        "Attachment",
        "Indexed component material overrides",
        "Resolved preview materials",
        "Animation class",
        "Tags",
    };

    private readonly NativeSuitPartRecord _part;
    private bool _skipPreviewLoad;
    private readonly RichTextBox _recipe;
    private readonly ModelPreviewControl _preview = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = Theme.OnDarkMuted,
        Font = Theme.Caption,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "Preparing 3D preview…",
    };

    public event EventHandler? ApplyRequested;

    public PartInspectorForm(NativeSuitPartRecord part)
    {
        _part = part;
        var (confidence, reason) = PartRecipeService.Confidence(part);

        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"Part inspector — {DisplayName(part)}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1120, 740);
        MinimumSize = new Size(860, 580);
        BackColor = Theme.WindowBg;
        Icon = EmbeddedAssets.LoadIcon("Icon.ico") ?? Icon;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            BackColor = Theme.WindowBg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(root);

        var header = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.FrameLine,
            CornerRadius = Theme.Radius,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(18, 10, 18, 9),
        };
        var headerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(headerLayout);
        headerLayout.Controls.Add(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = Theme.Eyebrow,
            ForeColor = Theme.Parts,
            Text = $"NATIVE PART  •  {part.Slot.ToUpperInvariant()}",
            TextAlign = ContentAlignment.BottomLeft,
        }, 0, 0);
        headerLayout.Controls.Add(new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = Theme.Title,
            ForeColor = Theme.OnDark,
            Text = DisplayName(part),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);
        var mode = BuildChip("READ-ONLY PREVIEW", Theme.Materials);
        mode.Margin = new Padding(12, 8, 0, 8);
        headerLayout.Controls.Add(mode, 1, 0);
        headerLayout.SetRowSpan(mode, 2);
        root.Controls.Add(header, 0, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.WindowBg,
            Margin = Padding.Empty,
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 348));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(main, 0, 1);

        var detailsCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.Radius,
            Margin = new Padding(0, 0, 10, 0),
            Padding = new Padding(14, 12, 14, 12),
        };
        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
        };
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailsCard.Controls.Add(details);
        details.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.Eyebrow,
            ForeColor = Theme.OnDarkMuted,
            Text = "INDEXED RECIPE",
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        var chips = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 6),
        };
        chips.Controls.Add(BuildChip(part.Context.ToUpperInvariant(), Theme.Base));
        chips.Controls.Add(BuildChip($"{confidence}".ToUpperInvariant(), Theme.Gold));
        details.Controls.Add(chips, 0, 1);

        var recipeSurface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.SlateDark,
            Padding = new Padding(10, 9, 5, 9),
        };
        _recipe = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SlateDark,
            ForeColor = Theme.OnDarkMuted,
            Font = Theme.Caption,
            DetectUrls = false,
            WordWrap = true,
        };
        recipeSurface.Controls.Add(_recipe);
        details.Controls.Add(recipeSurface, 0, 2);
        main.Controls.Add(detailsCard, 0, 0);
        SetRecipeText(Describe(part, reason));

        var viewerCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.Radius,
            Margin = Padding.Empty,
            Padding = new Padding(1),
        };
        var viewerHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.CardBg,
            Padding = new Padding(8, 7, 8, 6),
        };
        viewerHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        viewerHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        viewerHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        viewerHost.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.Eyebrow,
            ForeColor = Theme.OnDarkMuted,
            Text = "3D PREVIEW",
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        viewerHost.Controls.Add(_preview, 0, 1);
        viewerHost.Controls.Add(_status, 0, 2);
        viewerCard.Controls.Add(viewerHost);
        main.Controls.Add(viewerCard, 1, 0);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.WindowBg,
            Margin = new Padding(0, 10, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var primary = MakeButton("Use on character", primary: true, (_, _) => ApplyRequested?.Invoke(this, EventArgs.Empty));
        primary.Width = 150;
        var leftActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
        };
        leftActions.Controls.Add(primary);
        footer.Controls.Add(leftActions, 0, 0);
        var rightActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
        };
        rightActions.Controls.Add(MakeButton("Close", primary: false, (_, _) => Close()));
        rightActions.Controls.Add(MakeButton("Copy source", primary: false, (_, _) => Copy(_part.SourcePackagePath)));
        rightActions.Controls.Add(MakeButton("Copy mesh path", primary: false, (_, _) => Copy(_part.MeshObjectPath)));
        footer.Controls.Add(rightActions, 1, 0);
        root.Controls.Add(footer, 0, 2);

        Shown += async (_, _) =>
        {
            if (!_skipPreviewLoad)
            {
                await LoadPreviewAsync();
            }
        };
        FormClosed += (_, _) => _preview.ReleaseRenderer();
    }

    internal void ConfigureForUiAudit()
    {
        _skipPreviewLoad = true;
        _preview.ShowMessage("Native mesh and resolved materials appear here.");
        _status.Text = "Mesh defaults shown · drag to orbit · scroll to zoom";
    }

    private async Task LoadPreviewAsync()
    {
        _preview.ShowMessage("Decoding the native mesh and its materials…");
        try
        {
            var settings = AppSettings.Current;
            var inspection = await Task.Run(() => ModelPreviewService.BuildPartInspection(
                settings.EffectiveGamePaksRoot(),
                settings.EffectiveUsmapPath() ?? "",
                _part));
            if (IsDisposed)
            {
                return;
            }
            await _preview.ShowFolderAsync(inspection.PreviewFolder);
            var (_, reason) = PartRecipeService.Confidence(_part);
            SetRecipeText(Describe(_part, reason, inspection.Materials));
            var overrides = inspection.Materials.Count(material => material.IsComponentOverride);
            _status.Text = overrides == 0
                ? "Mesh defaults shown · drag to orbit · scroll to zoom"
                : $"{overrides} component material override(s) shown · drag to orbit · scroll to zoom";
        }
        catch (Exception ex)
        {
            if (IsDisposed)
            {
                return;
            }
            var detail = ex.Message.Split('\n')[0];
            _preview.ShowMessage("This part could not be previewed.\n\n" + detail);
            _status.Text = "Preview failed: " + detail;
        }
    }

    private void SetRecipeText(string text)
    {
        _recipe.Text = text;
        var offset = 0;
        foreach (var line in _recipe.Lines)
        {
            if (RecipeHeadings.Contains(line))
            {
                _recipe.Select(offset, line.Length);
                _recipe.SelectionColor = Theme.Gold;
                _recipe.SelectionFont = Theme.BodyStrong;
            }
            offset += line.Length + 1;
        }
        _recipe.Select(0, 0);
        _recipe.SelectionColor = Theme.OnDarkMuted;
    }

    private static RoundedPanel BuildChip(string text, Color accent)
    {
        var width = Math.Max(64, TextRenderer.MeasureText(text, Theme.Eyebrow).Width + 20);
        var chip = new RoundedPanel
        {
            Width = width,
            Height = 25,
            BackColor = Theme.Tint(accent),
            BorderColor = Theme.Blend(accent, Theme.FrameLine, 0.45),
            CornerRadius = 12,
            Margin = new Padding(0, 0, 7, 0),
        };
        chip.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.Eyebrow,
            ForeColor = accent,
            Text = text,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        return chip;
    }

    private static Button MakeButton(string text, bool primary, EventHandler click)
    {
        var button = new Button
        {
            AutoSize = true,
            Height = 36,
            MinimumSize = new Size(92, 36),
            Text = text,
            Font = Theme.Body,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10, 0, 10, 0),
        };
        if (primary)
        {
            Theme.StyleGoldButton(button);
        }
        else
        {
            Theme.StyleDarkButton(button);
        }
        Theme.RoundControl(button);
        button.Click += click;
        return button;
    }

    private static string Describe(
        NativeSuitPartRecord part,
        string recipeReason,
        IReadOnlyList<ModelPreviewService.PartPreviewMaterial>? resolvedMaterials = null)
    {
        var text = new StringBuilder();
        text.AppendLine(recipeReason);
        text.AppendLine();
        text.AppendLine("Mesh");
        text.AppendLine(part.MeshObjectPath);
        text.AppendLine();
        text.AppendLine("Source Blueprint");
        text.AppendLine(part.SourcePackagePath);
        text.AppendLine();
        text.AppendLine("Component");
        text.AppendLine($"{part.ComponentClass} · {part.ComponentTemplateExport}");
        text.AppendLine();
        text.AppendLine("Attachment");
        text.AppendLine(string.IsNullOrWhiteSpace(part.AttachSocket)
            ? "No socket"
            : $"{part.AttachSocket} on {(string.IsNullOrWhiteSpace(part.ParentComponentOrVariableName) ? "native parent" : part.ParentComponentOrVariableName)}");
        text.AppendLine();
        var indexedMaterials = part.Materials
            .Select((material, slot) => new
            {
                Slot = slot,
                Path = !string.IsNullOrWhiteSpace(material.ObjectPath)
                    ? material.ObjectPath
                    : string.IsNullOrWhiteSpace(material.PackagePath)
                        ? ""
                        : $"{material.PackagePath}.{material.ObjectName}",
            })
            .Where(material => !string.IsNullOrWhiteSpace(material.Path))
            .ToList();
        text.AppendLine("Indexed component material overrides");
        if (indexedMaterials.Count == 0)
        {
            text.AppendLine("None recorded. The preview uses the mesh's default material slots.");
        }
        else
        {
            foreach (var material in indexedMaterials)
            {
                text.AppendLine($"[{material.Slot}] {material.Path}");
            }
        }
        if (resolvedMaterials is not null)
        {
            text.AppendLine();
            text.AppendLine("Resolved preview materials");
            if (resolvedMaterials.Count == 0)
            {
                text.AppendLine("The mesh reports no material slots.");
            }
            foreach (var material in resolvedMaterials)
            {
                var source = material.IsComponentOverride ? "component override" : "mesh default";
                var path = string.IsNullOrWhiteSpace(material.MaterialPath) ? "<unassigned>" : material.MaterialPath;
                text.AppendLine($"[{material.Slot}] {path} ({source})");
            }
        }
        if (!string.IsNullOrWhiteSpace(part.AnimClassObjectPath))
        {
            text.AppendLine();
            text.AppendLine("Animation class");
            text.AppendLine(part.AnimClassObjectPath);
        }
        if (part.ComponentTags.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Tags");
            text.AppendLine(string.Join(", ", part.ComponentTags));
        }
        return text.ToString().TrimEnd();
    }

    private static string DisplayName(NativeSuitPartRecord part)
    {
        var name = UnrealPathUtil.AssetName(part.MeshObjectPath);
        return string.IsNullOrWhiteSpace(name) ? part.Slot : name;
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard may be busy */ }
    }
}
