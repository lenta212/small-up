using System.Numerics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client.UserInterface.Systems.Guidebook;

public sealed class FrontierTutorialOfferWindow : DefaultWindow
{
    private readonly Label _progressLabel;
    private readonly RichTextLabel _question;
    private readonly BoxContainer _topicList;

    public Button KnowButton { get; }
    public Button ExplainButton { get; }
    public Button LaterButton { get; }
    public event Action<int>? TopicSelected;

    public FrontierTutorialOfferWindow()
    {
        Title = Loc.GetString("frontier-tutorial-offer-title");
        MinSize = new Vector2(620, 520);
        SetSize = new Vector2(680, 580);
        Resizable = true;

        var description = new RichTextLabel
        {
            MinSize = new Vector2(560, 62),
            HorizontalExpand = true,
        };
        description.SetMessage(Loc.GetString("frontier-tutorial-offer-intro"));

        _progressLabel = new Label
        {
            HorizontalExpand = true,
        };

        _question = new RichTextLabel
        {
            MinSize = new Vector2(560, 50),
            HorizontalExpand = true,
        };

        _topicList = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        KnowButton = new Button
        {
            Text = Loc.GetString("frontier-tutorial-know"),
            HorizontalExpand = true,
        };
        ExplainButton = new Button
        {
            Text = Loc.GetString("frontier-tutorial-explain"),
            HorizontalExpand = true,
        };
        LaterButton = new Button
        {
            Text = Loc.GetString("frontier-tutorial-later"),
            HorizontalExpand = true,
        };

        Contents.AddChild(new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 10,
            Children =
            {
                description,
                _progressLabel,
                new PanelContainer
                {
                    StyleClasses = { "UiSurfaceSection" },
                    HorizontalExpand = true,
                    VerticalExpand = true,
                    Children =
                    {
                        new ScrollContainer
                        {
                            HorizontalExpand = true,
                            VerticalExpand = true,
                            HScrollEnabled = false,
                            VScrollEnabled = true,
                            ReserveScrollbarSpace = true,
                            Children = { _topicList },
                        },
                    },
                },
                new PanelContainer
                {
                    StyleClasses = { "UiNoticeInfo" },
                    HorizontalExpand = true,
                    Children =
                    {
                        new BoxContainer
                        {
                            Orientation = BoxContainer.LayoutOrientation.Vertical,
                            Margin = new Thickness(10, 8),
                            Children = { _question },
                        },
                    },
                },
                new BoxContainer
                {
                    Orientation = BoxContainer.LayoutOrientation.Horizontal,
                    SeparationOverride = 8,
                    Children = { KnowButton, ExplainButton, LaterButton },
                },
            },
        });
    }

    public void SetChecklist(IReadOnlyList<string> topics, int completedMask, int currentIndex)
    {
        var completed = 0;
        for (var index = 0; index < topics.Count; index++)
        {
            if ((completedMask & 1 << index) != 0)
                completed++;
        }

        _progressLabel.Text = Loc.GetString(
            "frontier-tutorial-topic-progress",
            ("completed", completed),
            ("total", topics.Count));

        _topicList.RemoveAllChildren();
        for (var index = 0; index < topics.Count; index++)
        {
            var capturedIndex = index;
            var isComplete = (completedMask & 1 << index) != 0;
            var isCurrent = index == currentIndex;
            var statusKey = isComplete
                ? "frontier-tutorial-topic-complete"
                : isCurrent
                    ? "frontier-tutorial-topic-current"
                    : "frontier-tutorial-topic-pending";
            var topicButton = new Button
            {
                Text = Loc.GetString(statusKey, ("topic", topics[index])),
                HorizontalExpand = true,
                MinHeight = 34,
                Disabled = isComplete,
            };
            if (isCurrent)
                topicButton.AddStyleClass("UiActionSafe");
            topicButton.OnPressed += _ => TopicSelected?.Invoke(capturedIndex);
            _topicList.AddChild(topicButton);
        }

        var currentTopic = currentIndex >= 0 && currentIndex < topics.Count
            ? topics[currentIndex]
            : Loc.GetString("frontier-tutorial-topic-none");
        _question.SetMessage(Loc.GetString(
            "frontier-tutorial-topic-question",
            ("topic", currentTopic)));
    }
}
