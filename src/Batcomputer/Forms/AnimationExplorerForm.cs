using System.Text;

namespace Batcomputer;

/// <summary>
/// Unified character-animation browser. It shows every readable action, montage, layer, and
/// locomotion sequence used by the selected gameplay donor alongside the imported animation
/// library. Mutations are requested from the owner so project saves stay transactional.
/// </summary>
public sealed class AnimationExplorerForm : AdaptiveForm
{
    private readonly NativeSuitProject? _project;
    private readonly AnimLibrary _library;
    private CharacterAnimationSnapshot _characterGraph;
    private readonly Dictionary<string, AnimLibraryEntry> _entries;
    private readonly string? _initialPackagePath;

    private readonly TextBox _search = new();
    private readonly TreeView _tree = new();
    private readonly Label _summary = new();
    private readonly Label _detailTitle = new();
    private readonly Label _detailSubtitle = new();
    private readonly RichTextBox _details = new();
    private readonly Label _applyHint = new();
    private readonly Button _replace = new();
    private readonly Button _reset = new();
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 180 };

    private AnimLibraryEntry? _selectedEntry;
    private CharacterAnimationTargetSnapshot? _selectedTarget;
    private CharacterAnimationSlotSnapshot? _selectedSlot;
    private bool _initialSelectionApplied;

    /// <summary>
    /// Raised when the user asks to replace or restore one exact target. The explorer never
    /// mutates the suit by itself.
    /// </summary>
    public event EventHandler<AnimationExplorerTargetRequestedEventArgs>? ReplaceRequested;
    public event EventHandler<AnimationExplorerTargetRequestedEventArgs>? ResetRequested;

    public AnimationExplorerForm(
        NativeSuitProject? project,
        AnimLibrary library,
        string? initialPackagePath = null,
        CharacterAnimationSnapshot? characterGraph = null)
    {
        _project = project;
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _characterGraph = characterGraph ?? new CharacterAnimationGraphService().Build(project);
        _initialPackagePath = initialPackagePath;
        _entries = library.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        Text = "Animation Explorer";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1120, 720);
        MinimumSize = new Size(820, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;

        BuildLayout();
        RebuildTree();
    }

    /// <summary>Re-reads the saved suit after the owner applies or resets an override.</summary>
    public void RefreshFromProject()
    {
        _characterGraph = new CharacterAnimationGraphService().Build(_project);
        RebuildTree();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            BackColor = Theme.WindowBg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildMain(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        _search.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildTree();
        };
        _tree.AfterSelect += (_, e) => ShowNode(e.Node?.Tag as AnimationExplorerNode);
        FormClosed += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Dispose();
        };

        _replace.Click += (_, _) => RequestReplace();
        _reset.Click += (_, _) => RequestReset();
    }

    private Control BuildHeader()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardHi,
            BorderColor = Theme.FrameLine,
            CornerRadius = Theme.Radius,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(18, 10, 16, 8),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "ANIMATION EXPLORER",
            Font = Theme.Eyebrow,
            ForeColor = Theme.Animations,
            TextAlign = ContentAlignment.BottomLeft,
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Browse every readable character action, layer, and locomotion sequence; select one exact target to replace or restore.",
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
        }, 0, 1);

        var chips = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(12, 10, 0, 0),
        };
        chips.Controls.Add(BuildChip(
            _project is null ? "NO SUIT OPEN" : ValueOr(_project.DisplayName, _project.SlotId).ToUpperInvariant(),
            _project is null ? Theme.OnDarkMuted : Theme.Base));
        var graphTargets = _characterGraph.Sets.Sum(set => set.Slots.Sum(slot => slot.Targets.Count)) +
                           _characterGraph.LocomotionSequences.Count;
        chips.Controls.Add(BuildChip($"{graphTargets} TARGETS", Theme.Base));
        chips.Controls.Add(BuildChip($"{_library.Entries.Count} IMPORTED", Theme.Animations));
        layout.Controls.Add(chips, 1, 0);
        layout.SetRowSpan(chips, 2);

        return card;
    }

    private Control BuildMain()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.WindowBg,
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildTreeCard(), 0, 0);
        layout.Controls.Add(BuildDetailCard(), 1, 0);
        return layout;
    }

    private Control BuildTreeCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.Radius,
            Margin = new Padding(0, 0, 2, 0),
            Padding = new Padding(12, 10, 12, 10),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "CHARACTER ANIMATION TREE",
            Font = Theme.Eyebrow,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Search action, context, character, package, rig or dependency…";
        _search.Margin = new Padding(0, 2, 0, 7);
        _search.Font = Theme.Body;
        Theme.StyleDarkInput(_search);
        layout.Controls.Add(_search, 0, 1);

        _tree.Dock = DockStyle.Fill;
        _tree.BackColor = Theme.SlateDark;
        _tree.ForeColor = Theme.OnDark;
        _tree.LineColor = Theme.SlateLight;
        _tree.BorderStyle = BorderStyle.None;
        _tree.HideSelection = false;
        _tree.FullRowSelect = true;
        _tree.ShowNodeToolTips = true;
        _tree.ShowRootLines = false;
        _tree.ItemHeight = Math.Max(24, TextRenderer.MeasureText("Ag", Theme.Body).Height + 7);
        _tree.Font = Theme.Body;
        layout.Controls.Add(_tree, 0, 2);

        _summary.Dock = DockStyle.Fill;
        _summary.Font = Theme.Caption;
        _summary.ForeColor = Theme.OnDarkMuted;
        _summary.TextAlign = ContentAlignment.BottomLeft;
        layout.Controls.Add(_summary, 0, 3);
        return card;
    }

    private Control BuildDetailCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.Radius,
            Margin = new Padding(6, 0, 0, 0),
            Padding = new Padding(14, 10, 14, 10),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "DETAILS",
            Font = Theme.Eyebrow,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        _detailTitle.Dock = DockStyle.Fill;
        _detailTitle.Font = Theme.Title;
        _detailTitle.ForeColor = Theme.OnDark;
        _detailTitle.AutoEllipsis = true;
        _detailTitle.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_detailTitle, 0, 1);

        _detailSubtitle.Dock = DockStyle.Fill;
        _detailSubtitle.Font = Theme.Caption;
        _detailSubtitle.ForeColor = Theme.OnDarkMuted;
        _detailSubtitle.AutoEllipsis = true;
        _detailSubtitle.TextAlign = ContentAlignment.TopLeft;
        layout.Controls.Add(_detailSubtitle, 0, 2);

        var surface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.SlateDark,
            Padding = new Padding(12, 10, 6, 10),
        };
        _details.Dock = DockStyle.Fill;
        _details.ReadOnly = true;
        _details.BorderStyle = BorderStyle.None;
        _details.BackColor = Theme.SlateDark;
        _details.ForeColor = Theme.OnDarkMuted;
        _details.Font = Theme.Mono;
        _details.WordWrap = true;
        _details.DetectUrls = false;
        surface.Controls.Add(_details);
        layout.Controls.Add(surface, 0, 3);

        ShowNode(null);
        return card;
    }

    private Control BuildFooter()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Theme.WindowBg,
            Padding = new Padding(0, 10, 0, 0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _applyHint.Dock = DockStyle.Fill;
        _applyHint.Text = "Select an animation under Current character, then choose Replace…";
        _applyHint.Font = Theme.Caption;
        _applyHint.ForeColor = Theme.OnDarkMuted;
        _applyHint.TextAlign = ContentAlignment.MiddleLeft;
        _applyHint.AutoEllipsis = true;
        layout.Controls.Add(_applyHint, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(8, 0, 8, 0),
        };
        ConfigureApplyButton(_reset, "Reset to donor", primary: false);
        ConfigureApplyButton(_replace, "Replace…", primary: true);
        actions.Controls.Add(_reset);
        actions.Controls.Add(_replace);
        layout.Controls.Add(actions, 1, 0);

        var close = new Button
        {
            Text = "Close",
            Width = 92,
            Height = 34,
            Margin = Padding.Empty,
            DialogResult = DialogResult.Cancel,
        };
        Theme.StyleDarkButton(close);
        layout.Controls.Add(close, 2, 0);
        CancelButton = close;

        SetTargetButtons(null);
        return layout;
    }

    private static void ConfigureApplyButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.Width = primary ? 118 : 132;
        button.Height = 34;
        button.Margin = new Padding(0, 0, 6, 0);
        if (primary)
        {
            Theme.StyleGoldButton(button);
        }
        else
        {
            Theme.StyleDarkButton(button);
        }
    }

    private void RebuildTree()
    {
        var previousEntry = _selectedEntry?.Id;
        var snapshot = AnimationExplorerSnapshotBuilder.Build(
            _project,
            _library,
            _characterGraph,
            _search.Text);

        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            foreach (var root in snapshot.Roots)
            {
                var treeRoot = BuildTreeNode(root);
                _tree.Nodes.Add(treeRoot);
                treeRoot.Expand();

                if (root.Title.Equals("Current character", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (TreeNode child in treeRoot.Nodes)
                    {
                        child.Expand();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_search.Text))
            {
                _tree.ExpandAll();
            }

            var desired = !_initialSelectionApplied && !string.IsNullOrWhiteSpace(_initialPackagePath)
                ? FindInitialEntryId(_initialPackagePath)
                : previousEntry;
            _initialSelectionApplied = true;
            SelectEntryNode(desired);

            if (_tree.SelectedNode is null && _tree.Nodes.Count > 0)
            {
                _tree.SelectedNode = _tree.Nodes[0];
            }
        }
        finally
        {
            _tree.EndUpdate();
        }

        var targetCount = CountTargetNodes(_tree.Nodes);
        _summary.Text = string.IsNullOrWhiteSpace(_search.Text)
            ? $"{targetCount} character target{Plural(targetCount)} • {snapshot.ImportedCount} imported"
            : $"{targetCount} matching target{Plural(targetCount)} • {CountImportedNodes(_tree.Nodes)} imported match{Plural(CountImportedNodes(_tree.Nodes))}";

        if (_tree.Nodes.Count == 0)
        {
            _selectedEntry = null;
            _selectedTarget = null;
            _selectedSlot = null;
            SetTargetButtons(null);
            _detailTitle.Text = "No matches";
            _detailSubtitle.Text = "Try an action, context, character, package, rig or animation name.";
            _details.Text = "Nothing in the current character graph or imported animation library matched this search.";
        }
    }

    private TreeNode BuildTreeNode(AnimationExplorerNode node)
    {
        var text = string.IsNullOrWhiteSpace(node.Value) ? node.Title : $"{node.Title}  —  {node.Value}";
        var result = new TreeNode(text)
        {
            Name = node.EntryId ?? "",
            Tag = node,
            ToolTipText = node.Value,
            ForeColor = NodeColor(node),
        };
        foreach (var child in node.Children)
        {
            result.Nodes.Add(BuildTreeNode(child));
        }
        return result;
    }

    private static Color NodeColor(AnimationExplorerNode node) => node.Kind switch
    {
        AnimationExplorerNodeKind.Section => Theme.Animations,
        AnimationExplorerNodeKind.CharacterSet => Theme.Base,
        AnimationExplorerNodeKind.AnimationSlot => Theme.OnDarkMuted,
        AnimationExplorerNodeKind.AnimationTarget when node.CharacterTarget?.IsOverridden == true => Theme.Good,
        AnimationExplorerNodeKind.AnimationTarget => Theme.OnDark,
        AnimationExplorerNodeKind.ImportedAnimation when node.CanApply => Theme.Good,
        AnimationExplorerNodeKind.ImportedAnimation => Theme.Warn,
        AnimationExplorerNodeKind.Warning => Theme.Crit,
        AnimationExplorerNodeKind.Diagnostic => Theme.Warn,
        AnimationExplorerNodeKind.Rig => Theme.Base,
        AnimationExplorerNodeKind.SupportPackage => Theme.Materials,
        _ => Theme.OnDark,
    };

    private void ShowNode(AnimationExplorerNode? node)
    {
        if (node is null)
        {
            _selectedEntry = null;
            _selectedTarget = null;
            _selectedSlot = null;
            _detailTitle.Text = "Choose a character animation";
            _detailSubtitle.Text = "Its exact action/context target and current asset will appear here.";
            _details.Text =
                "Current character\n" +
                "  Every readable action/montage, animation-blueprint layer, and locomotion sequence.\n\n" +
                "Imported animations\n" +
                "  Cooked custom sequences/montages and the support packages that travel with them.";
            SetTargetButtons(null);
            return;
        }

        _selectedEntry = !string.IsNullOrWhiteSpace(node.EntryId) && _entries.TryGetValue(node.EntryId, out var entry)
            ? entry
            : null;
        _selectedTarget = node.CharacterTarget;
        _selectedSlot = node.CharacterSlot;

        _detailTitle.Text = node.Title;
        _detailSubtitle.Text = node.Value;
        _details.Text = BuildDetails(node, _selectedEntry, _selectedTarget);

        var canReplace = node.Kind == AnimationExplorerNodeKind.AnimationTarget &&
                         _project is not null &&
                         _selectedTarget is not null &&
                         AnimationExplorerSnapshotBuilder.CanReplaceTarget(_selectedTarget);
        SetTargetButtons(canReplace ? _selectedTarget : null);
        _applyHint.Text = canReplace
            ? $"Replace {FriendlyTargetName(_selectedTarget!)} with a compatible base-game or imported animation."
            : _project is null
                ? "Open a suit before editing its character animations."
                : _selectedEntry is not null
                    ? "Imported entries are sources. Select a target under Current character, then choose Replace…"
                    : "Select an individual animation target—not its group or context row—to replace it.";
    }

    private static string BuildDetails(
        AnimationExplorerNode node,
        AnimLibraryEntry? entry,
        CharacterAnimationTargetSnapshot? target)
    {
        var text = new StringBuilder();
        text.AppendLine(node.Title.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(node.Value))
        {
            text.AppendLine(node.Value);
        }

        if (target is not null)
        {
            text.AppendLine();
            AddDetail(text, "Target type", target.ReferenceKind.ToString());
            AddDetail(text, "Action", node.CharacterSlot?.ActionTag ?? "Locomotion graph");
            AddDetail(
                text,
                "Context",
                node.CharacterSlot is { ContextTags.Count: > 0 }
                    ? string.Join(" + ", node.CharacterSlot.ContextTags)
                    : "Default");
            AddDetail(text, "Current asset", target.EffectivePackage);
            AddDetail(text, "Current class", target.EffectiveAssetClass);
            AddDetail(text, "Donor asset", target.OriginalPackage);
            AddDetail(text, "Owner", target.OwnerPackage);
            if (target.EntryIndex >= 0)
            {
                AddDetail(text, "Stable location", $"entry {target.EntryIndex}, variant {target.WeightIndex}, reference {Math.Max(0, target.LayerIndex)}");
            }
            AddDetail(text, "Status", target.IsOverridden ? "Changed for this suit" : "Using gameplay donor");
            return text.ToString().TrimEnd();
        }

        if (entry is null)
        {
            if (node.Children.Count > 0)
            {
                text.AppendLine();
                foreach (var child in node.Children)
                {
                    text.AppendLine($"{child.Title}: {child.Value}");
                }
            }
            return text.ToString().TrimEnd();
        }

        text.AppendLine();
        AddDetail(text, "Status", FriendlyHealth(entry));
        AddDetail(text, "Package", entry.PackagePath);
        AddDetail(text, "Rig / skeleton", entry.Skeleton);
        AddDetail(text, "Asset class", entry.AssetClass);
        AddDetail(text, "Category", entry.Category);
        AddDetail(text, "Delivery", entry.SourceMode);
        AddDetail(text, "Root motion", entry.RootMotion ? "Enabled" : "Disabled");
        AddDetail(text, "Additive mode", entry.AdditiveMode);
        AddDetail(text, "Support packages", entry.SupportPackages.Count.ToString());
        AddDetail(text, "Dependencies", entry.Dependencies.Count.ToString());
        AddDetail(text, "Version", entry.Version.ToString());

        if (entry.HealthIssues.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("HEALTH NOTES");
            foreach (var issue in entry.HealthIssues)
            {
                text.AppendLine($"• {issue}");
            }
        }

        if (entry.UnresolvedImports.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("UNRESOLVED IMPORTS");
            foreach (var unresolved in entry.UnresolvedImports)
            {
                text.AppendLine($"• {unresolved}");
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.Notes))
        {
            text.AppendLine();
            text.AppendLine("NOTES");
            text.AppendLine(entry.Notes);
        }

        return text.ToString().TrimEnd();
    }

    private static void AddDetail(StringBuilder text, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.AppendLine($"{label}: {value}");
        }
    }

    private void RequestReplace()
    {
        if (_project is null || _selectedTarget is null ||
            !AnimationExplorerSnapshotBuilder.CanReplaceTarget(_selectedTarget))
        {
            return;
        }
        ReplaceRequested?.Invoke(this, new AnimationExplorerTargetRequestedEventArgs(_selectedTarget, _selectedSlot));
    }

    private void RequestReset()
    {
        if (_project is null || _selectedTarget is not { IsOverridden: true })
        {
            return;
        }
        ResetRequested?.Invoke(this, new AnimationExplorerTargetRequestedEventArgs(_selectedTarget, _selectedSlot));
    }

    private void SetTargetButtons(CharacterAnimationTargetSnapshot? target)
    {
        _replace.Enabled = target is not null;
        _reset.Enabled = target?.IsOverridden == true;
    }

    private string? FindInitialEntryId(string packageOrId)
    {
        return _library.Entries.FirstOrDefault(entry =>
                   entry.Id.Equals(packageOrId, StringComparison.OrdinalIgnoreCase) ||
                   entry.PackagePath.Equals(packageOrId, StringComparison.OrdinalIgnoreCase) ||
                   entry.Name.Equals(packageOrId, StringComparison.OrdinalIgnoreCase))
               ?.Id;
    }

    private void SelectEntryNode(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return;
        }

        var node = FindNode(_tree.Nodes, item =>
            item.Tag is AnimationExplorerNode model &&
            model.Kind == AnimationExplorerNodeKind.ImportedAnimation &&
            entryId.Equals(model.EntryId, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            return;
        }

        node.EnsureVisible();
        _tree.SelectedNode = node;
    }

    private static TreeNode? FindNode(TreeNodeCollection nodes, Func<TreeNode, bool> predicate)
    {
        foreach (TreeNode node in nodes)
        {
            if (predicate(node)) return node;
            var child = FindNode(node.Nodes, predicate);
            if (child is not null) return child;
        }
        return null;
    }

    private static int CountImportedNodes(TreeNodeCollection nodes)
    {
        var count = 0;
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is AnimationExplorerNode { Kind: AnimationExplorerNodeKind.ImportedAnimation })
            {
                count++;
            }
            count += CountImportedNodes(node.Nodes);
        }
        return count;
    }

    private static int CountTargetNodes(TreeNodeCollection nodes)
    {
        var count = 0;
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is AnimationExplorerNode { Kind: AnimationExplorerNodeKind.AnimationTarget })
            {
                count++;
            }
            count += CountTargetNodes(node.Nodes);
        }
        return count;
    }

    private static string FriendlyTargetName(CharacterAnimationTargetSnapshot target)
    {
        var value = ValueOr(target.EffectiveObjectName, target.OriginalObjectName, "this animation");
        foreach (var prefix in new[] { "ABP_", "AM_", "A_" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        }
        return value.Replace('_', ' ');
    }

    private static Control BuildChip(string text, Color accent)
    {
        var width = Math.Min(210, Math.Max(88, TextRenderer.MeasureText(text, Theme.Caption).Width + 28));
        var chip = new RoundedPanel
        {
            Width = width,
            Height = 24,
            BackColor = Theme.Tint(accent),
            BorderColor = accent,
            CornerRadius = 12,
            Margin = new Padding(0, 0, 7, 0),
        };
        chip.Controls.Add(new StatusDot
        {
            Left = 9,
            Top = 8,
            Width = 8,
            Height = 8,
            DotColor = accent,
        });
        chip.Controls.Add(new Label
        {
            Left = 22,
            Top = 4,
            Width = width - 27,
            Height = 16,
            Text = text,
            Font = Theme.Caption,
            ForeColor = Theme.OnDark,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        });
        return chip;
    }

    private static string FriendlyHealth(AnimLibraryEntry entry)
    {
        if (!entry.IsAvailable || entry.HealthStatus.Equals("quarantined", StringComparison.OrdinalIgnoreCase))
        {
            return "Needs attention";
        }
        return string.IsNullOrWhiteSpace(entry.HealthStatus) ? "Available" : entry.HealthStatus;
    }

    private static string ValueOr(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string Plural(int count) => count == 1 ? "" : "s";
}

public sealed class AnimationExplorerTargetRequestedEventArgs : EventArgs
{
    public AnimationExplorerTargetRequestedEventArgs(
        CharacterAnimationTargetSnapshot target,
        CharacterAnimationSlotSnapshot? slot)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Slot = slot;
    }

    public CharacterAnimationTargetSnapshot Target { get; }
    public CharacterAnimationSlotSnapshot? Slot { get; }
}
