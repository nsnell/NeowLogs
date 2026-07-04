using Godot;

namespace NeowLogs.NeowLogsCode;

public static class UiRuntime
{
    private const double RefreshIntervalSeconds = 0.15;

    private enum MeterMode
    {
        Damage,
        Block
    }

    private static readonly MeterMode[] Modes =
    [
        MeterMode.Damage,
        MeterMode.Block
    ];

    private static PanelContainer? _overlay;
    private static HBoxContainer? _tabs;
    private static VBoxContainer? _rows;
    private static Label? _title;
    private static Label? _leader;
    private static Label? _emptyState;
    private static Button? _collapseButton;
    private static PanelContainer? _panel;
    private static PanelContainer? _highlight;
    private static readonly List<Control> _rowPool = [];
    private static IReadOnlyCollection<PlayerStats> _latestPlayers = [];
    private static MeterMode _mode = MeterMode.Damage;
    private static bool _collapsed;
    private static string? _editingPlayerId;
    private static bool _refreshScheduled;

    public static void EnsureAttached()
    {
        if (_overlay != null)
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        AttachTo(tree.Root);
    }

    public static void EnsureAttached(Node? context)
    {
        if (_overlay != null)
        {
            return;
        }

        AttachTo(context?.GetTree()?.Root);
    }

    public static void Refresh(IEnumerable<PlayerStats> players)
    {
        EnsureAttached();
        _latestPlayers = players.ToArray();
        ScheduleRender();
    }

    public static void ShowActHighlight(int act, IEnumerable<PlayerStats> players)
    {
        EnsureAttached();
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        _highlight?.QueueFree();
        var panel = new PanelContainer
        {
            Name = "NeowLogsActHighlight",
            CustomMinimumSize = new Vector2(460, 320),
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -230,
            OffsetRight = 230,
            OffsetTop = -160,
            OffsetBottom = 160,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ProcessMode = Node.ProcessModeEnum.Always
        };
        panel.AddThemeStyleboxOverride("panel", MakeHighlightStyle());

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        panel.AddChild(root);

        var header = new HBoxContainer();
        root.AddChild(header);
        var title = new Label
        {
            Text = $"Act {act} Progress",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.99f, 1, 1));
        header.AddChild(title);

        var close = new Button
        {
            Text = "Close",
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(72, 28)
        };
        close.Pressed += () =>
        {
            _highlight?.QueueFree();
            _highlight = null;
        };
        header.AddChild(close);

        var note = new Label { Text = "Snapshot from the run so far" };
        note.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.86f, 1));
        root.AddChild(note);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 14);
        root.AddChild(columns);
        columns.AddChild(MakeHighlightBoard("Damage", players.OrderByDescending(p => p.DamageContribution).Take(5), p => p.DamageContribution));
        columns.AddChild(MakeHighlightBoard("Support", players.OrderByDescending(p => p.BlockContribution).Take(5), p => p.BlockContribution));

        _highlight = panel;
        tree.Root.CallDeferred(Node.MethodName.AddChild, panel);
    }

    private static void AttachTo(Node? root)
    {
        if (_overlay != null || root == null)
        {
            return;
        }

        var overlay = BuildOverlay();
        _overlay = overlay;
        root.CallDeferred(Node.MethodName.AddChild, overlay);
        NeowLogsMod.Logger.Warn("NeowLogs meter overlay scheduled.");
    }

    private static PanelContainer BuildOverlay()
    {
        var overlay = new PanelContainer
        {
            Name = "NeowLogsMeter",
            CustomMinimumSize = new Vector2(330, 238),
            MouseFilter = Control.MouseFilterEnum.Stop,
            ProcessMode = Node.ProcessModeEnum.Always,
            AnchorLeft = 1,
            AnchorRight = 1,
            AnchorTop = 0,
            AnchorBottom = 0,
            OffsetLeft = -354,
            OffsetRight = -24,
            OffsetTop = 24,
            OffsetBottom = 262
        };
        _panel = overlay;

        overlay.AddThemeStyleboxOverride("panel", MakePanelStyle());

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);
        overlay.AddChild(root);

        var header = new HBoxContainer();
        root.AddChild(header);

        _title = new Label
        {
            Text = "NeowLogs",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true
        };
        _title.AddThemeColorOverride("font_color", new Color(0.97f, 0.98f, 1f, 1));
        header.AddChild(_title);

        _collapseButton = new Button
        {
            Text = "-",
            CustomMinimumSize = new Vector2(28, 24),
            FocusMode = Control.FocusModeEnum.None
        };
        _collapseButton.Pressed += ToggleCollapsed;
        header.AddChild(_collapseButton);

        _tabs = new HBoxContainer();
        _tabs.AddThemeConstantOverride("separation", 4);
        root.AddChild(_tabs);
        foreach (var mode in Modes)
        {
            _tabs.AddChild(MakeTab(mode));
        }

        _leader = new Label
        {
            Text = "Waiting for combat events...",
            ClipText = true
        };
        _leader.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.86f, 1));
        root.AddChild(_leader);

        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 3);
        root.AddChild(_rows);

        _emptyState = new Label { Text = "No player stats yet." };
        _emptyState.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.86f, 1));
        root.AddChild(_emptyState);

        RenderTabs();
        return overlay;
    }

    private static Button MakeTab(MeterMode mode)
    {
        var button = new Button
        {
            Text = ShortModeLabel(mode),
            ToggleMode = true,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(70, 26),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        button.Pressed += () =>
        {
            _mode = mode;
            Render();
        };
        return button;
    }

    private static void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        if (_collapseButton != null)
        {
            _collapseButton.Text = _collapsed ? "+" : "-";
        }

        if (_title != null && _collapsed)
        {
            _title.Text = "NeowLogs";
        }

        if (_tabs != null)
        {
            _tabs.Visible = !_collapsed;
        }

        if (_leader != null)
        {
            _leader.Visible = !_collapsed;
        }

        if (_rows != null)
        {
            _rows.Visible = !_collapsed;
        }

        if (_emptyState != null)
        {
            _emptyState.Visible = !_collapsed && !_latestPlayers.Any(HasAnyStats);
        }

        if (_panel != null)
        {
            if (_collapsed)
            {
                _panel.CustomMinimumSize = new Vector2(330, 40);
                _panel.OffsetBottom = _panel.OffsetTop + 40;
            }
            else
            {
                _panel.CustomMinimumSize = new Vector2(330, 238);
                _panel.OffsetBottom = _panel.OffsetTop + 238;
            }
        }
    }

    private static void Render()
    {
        if (_rows == null)
        {
            return;
        }

        RenderTabs();

        foreach (var child in _rows.GetChildren())
        {
            if (child is Control control)
            {
                control.Visible = false;
            }
        }

        var players = _latestPlayers
            .Where(HasAnyStats)
            .OrderByDescending(ValueForMode)
            .ThenBy(player => player.Name)
            .Take(8)
            .ToArray();

        if (_emptyState != null)
        {
            _emptyState.Visible = !_collapsed && players.Length == 0;
        }

        var leader = players.FirstOrDefault();
        if (_title != null)
        {
            _title.Text = _collapsed ? "NeowLogs" : $"{ModeLabel(_mode)} Overall";
        }

        if (_leader != null)
        {
            _leader.Text = leader == null
                ? "Waiting for combat events..."
                : $"Leader: {leader.Name} - {ValueForMode(leader):0}";
        }

        if (_collapsed)
        {
            return;
        }

        var max = players.Select(ValueForMode).DefaultIfEmpty(0).Max();
        var rank = 1;
        foreach (var player in players)
        {
            var row = GetRow(rank - 1);
            UpdateRow(row, rank, player, ValueForMode(player), max);
            row.Visible = true;
            rank += 1;
        }
    }

    private static void ScheduleRender()
    {
        if (_refreshScheduled)
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Render();
            return;
        }

        _refreshScheduled = true;
        tree.CreateTimer(RefreshIntervalSeconds).Timeout += () =>
        {
            try
            {
                _refreshScheduled = false;
                Render();
            }
            catch (Exception ex)
            {
                _refreshScheduled = false;
                NeowLogsMod.Logger.Warn($"NeowLogs meter render failed: {ex.Message}");
            }
        };
    }

    private static void RenderTabs()
    {
        if (_tabs == null)
        {
            return;
        }

        for (var i = 0; i < _tabs.GetChildCount() && i < Modes.Length; i++)
        {
            if (_tabs.GetChild(i) is Button button)
            {
                button.ButtonPressed = Modes[i] == _mode;
                button.AddThemeStyleboxOverride("normal", MakeTabStyle(Modes[i] == _mode));
                button.AddThemeStyleboxOverride("pressed", MakeTabStyle(true));
                button.AddThemeStyleboxOverride("hover", MakeTabStyle(true));
                button.AddThemeColorOverride("font_color", Modes[i] == _mode ? new Color(0.98f, 0.99f, 1, 1) : new Color(0.7f, 0.76f, 0.84f, 1));
            }
        }
    }

    private static Control MakeRow(int rank, PlayerStats player, double value, double max)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 22) };
        row.AddThemeConstantOverride("separation", 5);

        AddLabel(row, $"{rank}", 22, HorizontalAlignment.Right, new Color(0.95f, 0.78f, 0.36f, 1));
        if (_editingPlayerId == player.Id)
        {
            var editor = new LineEdit
            {
                Text = player.Name,
                CustomMinimumSize = new Vector2(98, 0),
                SelectAllOnFocus = true
            };
            editor.TextSubmitted += text =>
            {
                _editingPlayerId = null;
                NeowLogsMod.Recorder.RenamePlayer(player.Id, text);
                Render();
            };
            editor.FocusExited += () =>
            {
                _editingPlayerId = null;
                Render();
            };
            row.AddChild(editor);
            editor.CallDeferred(Control.MethodName.GrabFocus);
        }
        else
        {
            var nameButton = new Button
            {
                Text = player.Name,
                CustomMinimumSize = new Vector2(98, 0),
                ClipText = true,
                FocusMode = Control.FocusModeEnum.None,
                Flat = true
            };
            nameButton.AddThemeColorOverride("font_color", PlayerColor(player));
            nameButton.Pressed += () =>
            {
                _editingPlayerId = player.Id;
                Render();
            };
            row.AddChild(nameButton);
        }

        row.AddChild(MakeStackedBar(player, max));

        AddLabel(row, $"{value:0}", 54, HorizontalAlignment.Right, new Color(0.96f, 0.97f, 1, 1));
        return row;
    }

    private static Control GetRow(int index)
    {
        if (_rows == null)
        {
            return new HBoxContainer();
        }

        while (_rowPool.Count <= index)
        {
            var row = MakeReusableRow();
            _rowPool.Add(row);
            _rows.AddChild(row);
        }

        return _rowPool[index];
    }

    private static Control MakeReusableRow()
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 22) };
        row.AddThemeConstantOverride("separation", 5);

        var rank = MakeLabel("", 22, HorizontalAlignment.Right, new Color(0.95f, 0.78f, 0.36f, 1));
        rank.Name = "Rank";
        row.AddChild(rank);

        var nameButton = new Button
        {
            Name = "Name",
            CustomMinimumSize = new Vector2(98, 0),
            ClipText = true,
            FocusMode = Control.FocusModeEnum.None,
            Flat = true
        };
        nameButton.Pressed += () =>
        {
            _editingPlayerId = nameButton.GetMeta("player_id").ToString();
            Render();
        };
        row.AddChild(nameButton);

        var editor = new LineEdit
        {
            Name = "Editor",
            CustomMinimumSize = new Vector2(98, 0),
            SelectAllOnFocus = true,
            Visible = false
        };
        editor.TextSubmitted += text =>
        {
            var playerId = editor.GetMeta("player_id").ToString();
            _editingPlayerId = null;
            NeowLogsMod.Recorder.RenamePlayer(playerId, text);
            Render();
        };
        editor.FocusExited += () =>
        {
            _editingPlayerId = null;
            Render();
        };
        row.AddChild(editor);

        var value = MakeLabel("", 54, HorizontalAlignment.Right, new Color(0.96f, 0.97f, 1, 1));
        value.Name = "Value";
        row.AddChild(MakeReusableStackedBar(value));
        row.AddChild(value);
        return row;
    }

    private static Label MakeLabel(string text, int width, HorizontalAlignment alignment, Color color)
    {
        var label = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(width, 0),
            ClipText = true,
            HorizontalAlignment = alignment
        };
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static void UpdateRow(Control row, int rank, PlayerStats player, double value, double max)
    {
        if (row.GetNodeOrNull<Label>("Rank") is { } rankLabel)
        {
            rankLabel.Text = $"{rank}";
        }

        var nameButton = row.GetNodeOrNull<Button>("Name");
        var editor = row.GetNodeOrNull<LineEdit>("Editor");
        if (_editingPlayerId == player.Id)
        {
            if (nameButton != null)
            {
                nameButton.Visible = false;
            }
            if (editor != null)
            {
                editor.Visible = true;
                if (!editor.HasFocus())
                {
                    editor.Text = player.Name;
                }
                editor.SetMeta("player_id", player.Id);
                if (!editor.HasFocus())
                {
                    editor.CallDeferred(Control.MethodName.GrabFocus);
                }
            }
        }
        else
        {
            if (editor != null)
            {
                editor.Visible = false;
            }
            if (nameButton != null)
            {
                nameButton.Visible = true;
                nameButton.Text = player.Name;
                nameButton.SetMeta("player_id", player.Id);
                nameButton.AddThemeColorOverride("font_color", PlayerColor(player));
            }
        }

        UpdateStackedBar(row.GetNodeOrNull<PanelContainer>("Bar"), player, max);

        if (row.GetNodeOrNull<Label>("Value") is { } valueLabel)
        {
            valueLabel.Text = $"{value:0}";
        }
    }

    private static void AddLabel(HBoxContainer row, string text, int width, HorizontalAlignment alignment, Color color)
    {
        var label = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(width, 0),
            ClipText = true,
            HorizontalAlignment = alignment
        };
        label.AddThemeColorOverride("font_color", color);
        row.AddChild(label);
    }

    private static bool HasAnyStats(PlayerStats player)
    {
        if (IsCompanionRow(player))
        {
            return false;
        }

        return player.Damage > 0
            || player.Block > 0
            || player.DamageAssist > 0
            || player.PreventedDamage > 0
            || player.DamageTaken > 0
            || player.Healing > 0
            || player.CardsPlayed > 0;
    }

    private static bool IsCompanionRow(PlayerStats player)
    {
        return player.Name.Contains("Otsy", StringComparison.OrdinalIgnoreCase)
            || player.Id.Contains("Otsy", StringComparison.OrdinalIgnoreCase);
    }

    private static double ValueForMode(PlayerStats player)
    {
        return _mode switch
        {
            MeterMode.Damage => player.DamageContribution,
            MeterMode.Block => player.BlockContribution,
            _ => player.Damage
        };
    }

    private static double PrimaryValueForMode(PlayerStats player)
    {
        return _mode switch
        {
            MeterMode.Damage => player.DamageBarTotal > 0 ? player.DirectDamage : player.Damage,
            MeterMode.Block => player.Block,
            _ => player.Damage
        };
    }

    private static double AssistValueForMode(PlayerStats player)
    {
        return _mode switch
        {
            MeterMode.Damage => player.DamageAssist,
            MeterMode.Block => player.PreventedDamage,
            _ => 0
        };
    }

    private static double PoisonValueForMode(PlayerStats player)
    {
        return _mode == MeterMode.Damage ? player.PoisonDamage : 0;
    }

    private static double DoomValueForMode(PlayerStats player)
    {
        return _mode == MeterMode.Damage ? player.DoomDamage : 0;
    }

    private static double PotionValueForMode(PlayerStats player)
    {
        return _mode == MeterMode.Damage ? player.PotionDamage : 0;
    }

    private static double CompanionValueForMode(PlayerStats player)
    {
        return _mode switch
        {
            MeterMode.Damage => player.CompanionDamage,
            MeterMode.Block => player.CompanionBlock,
            _ => 0
        };
    }

    private static string ModeLabel(MeterMode mode)
    {
        return mode switch
        {
            MeterMode.Damage => "Damage",
            MeterMode.Block => "Support",
            _ => "Damage"
        };
    }

    private static string ShortModeLabel(MeterMode mode)
    {
        return mode switch
        {
            MeterMode.Damage => "Dmg",
            MeterMode.Block => "Support",
            _ => "Dmg"
        };
    }

    private static Control MakeStackedBar(PlayerStats player, double max)
    {
        const int totalWidth = 116;
        var primary = PrimaryValueForMode(player);
        var assist = AssistValueForMode(player);
        var poison = PoisonValueForMode(player);
        var doom = DoomValueForMode(player);
        var potion = PotionValueForMode(player);
        var companion = CompanionValueForMode(player);
        var widths = SegmentWidths(totalWidth, max, primary, assist, poison, doom, potion, companion);

        var frame = new PanelContainer
        {
            CustomMinimumSize = new Vector2(totalWidth, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = TotalTooltipForMode(primary, assist, poison, doom, potion, companion)
        };
        frame.AddThemeStyleboxOverride("panel", MakeBarBackground());

        var stack = new HBoxContainer();
        stack.AddThemeConstantOverride("separation", 0);
        frame.AddChild(stack);

        AddBarSegment(stack, widths[0], PlayerColor(player), PrimaryTooltipForMode(primary));
        AddBarSegment(stack, widths[1], new Color(0.96f, 0.78f, 0.35f, 1), AssistTooltipForMode(assist));
        AddBarSegment(stack, widths[2], new Color(0.55f, 0.9f, 0.42f, 1), PoisonTooltipForMode(poison));
        AddBarSegment(stack, widths[3], new Color(0.74f, 0.48f, 1f, 1), DoomTooltipForMode(doom));
        AddBarSegment(stack, widths[4], new Color(1f, 0.56f, 0.44f, 1), PotionTooltipForMode(potion));
        AddBarSegment(stack, widths[5], new Color(0.92f, 0.68f, 1f, 1), CompanionTooltipForMode(companion));
        return frame;
    }

    private static PanelContainer MakeReusableStackedBar(Label valueLabel)
    {
        var frame = new PanelContainer
        {
            Name = "Bar",
            CustomMinimumSize = new Vector2(116, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        frame.AddThemeStyleboxOverride("panel", MakeBarBackground());
        var stack = new HBoxContainer { Name = "Stack" };
        stack.AddThemeConstantOverride("separation", 0);
        frame.AddChild(stack);

        var primary = MakeBarSegment("Primary", new Color(1, 1, 1, 1), valueLabel);
        var assist = MakeBarSegment("Assist", new Color(0.96f, 0.78f, 0.35f, 1), valueLabel);
        var poison = MakeBarSegment("Poison", new Color(0.55f, 0.9f, 0.42f, 1), valueLabel);
        var doom = MakeBarSegment("Doom", new Color(0.74f, 0.48f, 1f, 1), valueLabel);
        var potion = MakeBarSegment("Potion", new Color(1f, 0.56f, 0.44f, 1), valueLabel);
        var companion = MakeBarSegment("Companion", new Color(0.92f, 0.68f, 1f, 1), valueLabel);
        stack.AddChild(primary);
        stack.AddChild(assist);
        stack.AddChild(poison);
        stack.AddChild(doom);
        stack.AddChild(potion);
        stack.AddChild(companion);
        return frame;
    }

    private static void UpdateStackedBar(PanelContainer? frame, PlayerStats player, double max)
    {
        if (frame == null || frame.GetNodeOrNull<HBoxContainer>("Stack") is not { } stack)
        {
            return;
        }

        const int totalWidth = 116;
        var primary = PrimaryValueForMode(player);
        var assist = AssistValueForMode(player);
        var poison = PoisonValueForMode(player);
        var doom = DoomValueForMode(player);
        var potion = PotionValueForMode(player);
        var companion = CompanionValueForMode(player);
        var widths = SegmentWidths(totalWidth, max, primary, assist, poison, doom, potion, companion);

        frame.SetMeta("total_value_text", $"{ValueForMode(player):0}");
        frame.TooltipText = TotalTooltipForMode(primary, assist, poison, doom, potion, companion);
        UpdateBarSegment(stack.GetNodeOrNull<PanelContainer>("Primary"), widths[0], PlayerColor(player), PrimaryTooltipForMode(primary));
        UpdateBarSegment(stack.GetNodeOrNull<PanelContainer>("Assist"), widths[1], new Color(0.96f, 0.78f, 0.35f, 1), AssistTooltipForMode(assist));
        UpdateBarSegment(stack.GetNodeOrNull<PanelContainer>("Poison"), widths[2], new Color(0.55f, 0.9f, 0.42f, 1), PoisonTooltipForMode(poison));
        UpdateBarSegment(stack.GetNodeOrNull<PanelContainer>("Doom"), widths[3], new Color(0.74f, 0.48f, 1f, 1), DoomTooltipForMode(doom));
        UpdateBarSegment(stack.GetNodeOrNull<PanelContainer>("Potion"), widths[4], new Color(1f, 0.56f, 0.44f, 1), PotionTooltipForMode(potion));
        UpdateBarSegment(stack.GetNodeOrNull<PanelContainer>("Companion"), widths[5], new Color(0.92f, 0.68f, 1f, 1), CompanionTooltipForMode(companion));
    }

    private static void AddBarSegment(HBoxContainer stack, int width, Color color, string tooltip)
    {
        if (width <= 0)
        {
            return;
        }

        var segment = new PanelContainer
        {
            CustomMinimumSize = new Vector2(width, 14),
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = tooltip
        };
        segment.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = 2,
            CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2,
            CornerRadiusBottomRight = 2
        });
        stack.AddChild(segment);
    }

    private static PanelContainer MakeBarSegment(string name, Color color, Label valueLabel)
    {
        var segment = new PanelContainer
        {
            Name = name,
            CustomMinimumSize = new Vector2(0, 14),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        segment.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = 2,
            CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2,
            CornerRadiusBottomRight = 2
        });
        segment.MouseEntered += () => ApplyBarHoverValue(segment, valueLabel);
        segment.MouseExited += () => RestoreBarHoverValue(segment, valueLabel);
        return segment;
    }

    private static void UpdateBarSegment(PanelContainer? segment, int width, Color color, string tooltip)
    {
        if (segment == null)
        {
            return;
        }

        segment.Visible = width > 0;
        segment.CustomMinimumSize = new Vector2(Math.Max(0, width), 14);
        segment.TooltipText = tooltip;
        var separator = tooltip.LastIndexOf(" - ", StringComparison.Ordinal);
        segment.SetMeta("hover_value_text", width > 0 && separator >= 0 ? tooltip[(separator + 3)..] : "");
        segment.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = 2,
            CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2,
            CornerRadiusBottomRight = 2
        });
    }

    private static void ApplyBarHoverValue(PanelContainer segment, Label valueLabel)
    {
        var hoverText = segment.HasMeta("hover_value_text") ? segment.GetMeta("hover_value_text").ToString() : "";
        if (!string.IsNullOrWhiteSpace(hoverText))
        {
            valueLabel.Text = hoverText;
        }
    }

    private static void RestoreBarHoverValue(PanelContainer segment, Label valueLabel)
    {
        if (segment.GetParent()?.GetParent() is PanelContainer frame)
        {
            var totalText = frame.HasMeta("total_value_text") ? frame.GetMeta("total_value_text").ToString() : "";
            if (!string.IsNullOrWhiteSpace(totalText))
            {
                valueLabel.Text = totalText;
            }
        }
    }

    private static int[] SegmentWidths(int totalWidth, double max, params double[] values)
    {
        var denominator = Math.Max(1, max);
        var widths = values
            .Select(value => value <= 0 ? 0 : Math.Max(3, (int)Math.Round(totalWidth * Math.Clamp(value / denominator, 0, 1))))
            .ToArray();
        var total = widths.Sum();
        while (total > totalWidth)
        {
            var index = Array.LastIndexOf(widths, widths.Max());
            if (index < 0 || widths[index] <= 0)
            {
                break;
            }

            widths[index] -= 1;
            total -= 1;
        }

        return widths;
    }

    private static string PrimaryTooltipForMode(double value)
    {
        return _mode switch
        {
            MeterMode.Damage => $"Direct Damage - {value:0}",
            MeterMode.Block => $"Direct Defense - {value:0}",
            _ => $"Direct Damage - {value:0}"
        };
    }

    private static string AssistTooltipForMode(double value)
    {
        return _mode switch
        {
            MeterMode.Damage => $"Vuln Damage - {value:0}",
            MeterMode.Block => $"Utility Defense - {value:0}",
            _ => $"Utility - {value:0}"
        };
    }

    private static string TotalTooltipForMode(double primary, double assist, double poison = 0, double doom = 0, double potion = 0, double companion = 0)
    {
        return _mode switch
        {
            MeterMode.Damage => $"Total Damage - {primary + assist + poison + doom + potion + companion:0}",
            MeterMode.Block => $"Total Defense - {primary + assist + companion:0}",
            _ => $"Total - {primary + assist:0}"
        };
    }

    private static string PoisonTooltipForMode(double value)
    {
        return _mode == MeterMode.Damage ? $"Poison Damage - {value:0}" : "";
    }

    private static string DoomTooltipForMode(double value)
    {
        return _mode == MeterMode.Damage ? $"Doom Damage - {value:0}" : "";
    }

    private static string PotionTooltipForMode(double value)
    {
        return _mode == MeterMode.Damage ? $"Potion Damage - {value:0}" : "";
    }

    private static string CompanionTooltipForMode(double value)
    {
        return _mode switch
        {
            MeterMode.Damage => $"Otsy Damage - {value:0}",
            MeterMode.Block => $"Otsy Block - {value:0}",
            _ => ""
        };
    }

    private static Control MakeHighlightBoard(string title, IEnumerable<PlayerStats> players, Func<PlayerStats, double> value)
    {
        var box = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(210, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        box.AddThemeConstantOverride("separation", 5);
        var label = new Label { Text = title };
        label.AddThemeColorOverride("font_color", new Color(0.95f, 0.78f, 0.36f, 1));
        box.AddChild(label);

        var rank = 1;
        foreach (var player in players.Where(HasAnyStats))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            AddLabel(row, $"{rank}", 20, HorizontalAlignment.Right, new Color(0.95f, 0.78f, 0.36f, 1));
            AddLabel(row, player.Name, 116, HorizontalAlignment.Left, PlayerColor(player));
            AddLabel(row, $"{value(player):0}", 54, HorizontalAlignment.Right, new Color(0.96f, 0.97f, 1, 1));
            box.AddChild(row);
            rank += 1;
        }

        if (rank == 1)
        {
            var empty = new Label { Text = "No data yet." };
            empty.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.86f, 1));
            box.AddChild(empty);
        }

        return box;
    }

    private static Color PlayerColor(PlayerStats player)
    {
        var palette = new[]
        {
            new Color(0.35f, 0.78f, 1f, 1),
            new Color(1f, 0.56f, 0.72f, 1),
            new Color(0.91f, 0.78f, 0.32f, 1),
            new Color(0.65f, 0.94f, 0.44f, 1),
            new Color(0.78f, 0.58f, 1f, 1),
            new Color(1f, 0.67f, 0.35f, 1),
            new Color(0.48f, 0.92f, 0.82f, 1),
            new Color(0.9f, 0.9f, 0.96f, 1)
        };
        var key = string.IsNullOrWhiteSpace(player.Id) ? player.Name : player.Id;
        var hash = 0;
        foreach (var ch in key)
        {
            hash = unchecked(hash * 31 + ch);
        }

        return palette[(int)((uint)hash % palette.Length)];
    }

    private static StyleBoxFlat MakePanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.035f, 0.04f, 0.05f, 0.88f),
            BorderColor = new Color(0.18f, 0.22f, 0.29f, 0.95f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 9,
            ContentMarginTop = 8,
            ContentMarginRight = 9,
            ContentMarginBottom = 8
        };
    }

    private static StyleBoxFlat MakeHighlightStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.035f, 0.04f, 0.05f, 0.94f),
            BorderColor = new Color(0.55f, 0.7f, 0.95f, 0.95f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 16,
            ContentMarginTop = 14,
            ContentMarginRight = 16,
            ContentMarginBottom = 14
        };
    }

    private static StyleBoxFlat MakeTabStyle(bool active)
    {
        return new StyleBoxFlat
        {
            BgColor = active ? new Color(0.18f, 0.24f, 0.34f, 0.95f) : new Color(0.09f, 0.11f, 0.15f, 0.9f),
            BorderColor = active ? new Color(0.55f, 0.7f, 0.95f, 0.95f) : new Color(0.22f, 0.27f, 0.35f, 0.85f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            ContentMarginLeft = 4,
            ContentMarginTop = 2,
            ContentMarginRight = 4,
            ContentMarginBottom = 2
        };
    }

    private static StyleBoxFlat MakeBarBackground()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 0.95f),
            CornerRadiusTopLeft = 2,
            CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2,
            CornerRadiusBottomRight = 2
        };
    }

    private static StyleBoxFlat MakeBarFill(PlayerStats player)
    {
        return new StyleBoxFlat
        {
            BgColor = PlayerColor(player),
            CornerRadiusTopLeft = 2,
            CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2,
            CornerRadiusBottomRight = 2
        };
    }
}
