using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._LuaM.Materials;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._LuaM.Materials.UI;

public sealed class LuaMResourceStorageCrateWindow : FancyWindow
{
    public event Action<string, int, bool, LuaMResourceTransferDirection>? TransferRequested;

    private readonly Label _connection = new();
    private readonly Label _capacity = new();
    private readonly Label _status = new();
    private readonly BoxContainer _materials = new()
    {
        Orientation = BoxContainer.LayoutOrientation.Vertical,
        HorizontalExpand = true,
    };

    public LuaMResourceStorageCrateWindow()
    {
        Title = Loc.GetString("luam-resource-crate-ui-title");
        Resizable = true;
        MinSize = new Vector2(560, 360);
        SetSize = new Vector2(700, 560);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            Margin = new Thickness(8),
        };
        ContentsContainer.AddChild(root);
        root.AddChild(_connection);
        root.AddChild(_capacity);
        _status.StyleClasses.Add("LabelSubText");
        _status.Margin = new Thickness(0, 4, 0, 8);
        root.AddChild(_status);

        var scroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            HScrollEnabled = false,
        };
        scroll.AddChild(_materials);
        root.AddChild(scroll);
    }

    public void UpdateState(LuaMResourceStorageCrateUiState state)
    {
        _connection.Text = state.Linked
            ? Loc.GetString("luam-resource-crate-ui-linked", ("silo", state.SiloName))
            : Loc.GetString("luam-resource-crate-ui-unlinked");
        _capacity.Text = Loc.GetString("luam-resource-crate-ui-capacity",
            ("used", state.Used),
            ("capacity", state.Capacity));
        _status.Text = state.Status;
        _materials.RemoveAllChildren();

        if (state.Materials.Count == 0)
        {
            _materials.AddChild(new Label { Text = Loc.GetString("luam-resource-crate-ui-empty") });
            return;
        }

        foreach (var material in state.Materials)
        {
            var group = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Vertical,
                HorizontalExpand = true,
                Margin = new Thickness(0, 0, 0, 8),
            };
            group.AddChild(new Label
            {
                Text = Loc.GetString("luam-resource-crate-ui-material",
                    ("material", material.Name),
                    ("crate", material.CrateAmount),
                    ("silo", material.SiloAmount)),
            });

            var buttons = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Horizontal,
                HorizontalExpand = true,
            };
            var amount = new LineEdit
            {
                PlaceHolder = Loc.GetString("luam-resource-crate-ui-amount"),
                Text = "1000",
                MinWidth = 90,
                Margin = new Thickness(0, 0, 4, 0),
            };
            buttons.AddChild(amount);
            AddTransferButton(buttons, material.Id, Loc.GetString("luam-resource-crate-ui-to-crate"),
                () => ParseAmount(amount), false,
                LuaMResourceTransferDirection.SiloToCrate, !state.Linked || material.SiloAmount <= 0);
            AddTransferButton(buttons, material.Id, "всё", 0, true,
                LuaMResourceTransferDirection.SiloToCrate, !state.Linked || material.SiloAmount <= 0);
            AddTransferButton(buttons, material.Id, Loc.GetString("luam-resource-crate-ui-to-silo"),
                () => ParseAmount(amount), false,
                LuaMResourceTransferDirection.CrateToSilo, !state.Linked || material.CrateAmount <= 0);
            AddTransferButton(buttons, material.Id, "всё", 0, true,
                LuaMResourceTransferDirection.CrateToSilo, !state.Linked || material.CrateAmount <= 0);
            group.AddChild(buttons);
            _materials.AddChild(group);
        }
    }

    private void AddTransferButton(
        Control parent,
        string material,
        string text,
        Func<int> amount,
        bool transferAll,
        LuaMResourceTransferDirection direction,
        bool disabled)
    {
        var button = new Button
        {
            Text = text,
            Disabled = disabled,
            Margin = new Thickness(0, 0, 4, 0),
        };
        button.OnPressed += _ => TransferRequested?.Invoke(material, amount(), transferAll, direction);
        parent.AddChild(button);
    }

    private void AddTransferButton(
        Control parent,
        string material,
        string text,
        int amount,
        bool transferAll,
        LuaMResourceTransferDirection direction,
        bool disabled)
    {
        AddTransferButton(parent, material, text, () => amount, transferAll, direction, disabled);
    }

    private static int ParseAmount(LineEdit input)
    {
        return int.TryParse(input.Text, out var amount) ? Math.Max(0, amount) : 0;
    }
}
