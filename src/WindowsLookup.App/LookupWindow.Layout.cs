using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace WindowsLookup.App;

internal sealed partial class LookupWindow
{
    private const int MinCardWidth = 190;
    private const int MaxCardWidth = 380;
    private const int MinCardHeight = 88;
    private const int MaxCardHeight = 320;
    private const double HorizontalPadding = 32;

    private readonly Grid _root = new();
    private readonly Grid _titleDragArea = new()
    {
        Height = 32,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new SolidColorBrush(Colors.Transparent),
    };
    private readonly StackPanel _headerHost = new() { Spacing = 0 };
    private readonly StackPanel _wordHeader = new() { Spacing = 0, Visibility = Visibility.Collapsed };
    private readonly Border _phoneticBadge = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _phonetic = new() { IsTextSelectionEnabled = true, MaxWidth = 264 };
    private readonly Border _wordDivider = new();
    private readonly StackPanel _statusHeader = new() { Spacing = 0, Visibility = Visibility.Collapsed };
    private readonly StackPanel _statusLine = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 7,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ProgressRing _loadingRing = new() { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly FontIcon _statusIcon = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _statusTitle = new();
    private readonly Border _statusDivider = new() { Visibility = Visibility.Collapsed };
    private readonly Border _bodyCard = new();
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private readonly StackPanel _actions = new() { Spacing = 6, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private InputNonClientPointerSource? _nonClientPointer;

    // 创建没有系统标题栏的查词卡片布局。
    private Grid _buildLayout()
    {
        _applyOptionalStyle(_heading, "LookupHeadwordStyle");
        _applyOptionalStyle(_phonetic, "LookupPhoneticStyle");
        _applyOptionalStyle(_phoneticBadge, "LookupPhoneticBadgeStyle");
        _applyOptionalStyle(_wordDivider, "LookupDividerStyle");
        _applyOptionalStyle(_statusTitle, "LookupEyebrowStyle");
        _applyOptionalStyle(_statusDivider, "LookupDividerStyle");
        _applyOptionalStyle(_notice, "LookupNoticeTextStyle");
        _applyOptionalStyle(_speak, "LookupCompactButtonStyle");

        _phoneticBadge.Margin = new Thickness(0, 0, 8, 0);
        _wordDivider.Margin = new Thickness(0, 9, 0, 0);
        _statusDivider.Margin = new Thickness(0, 8, 0, 0);

        _speak.Content = _buildSpeakContent();
        AutomationProperties.SetName(_speak, "美式发音");
        ToolTipService.SetToolTip(_speak, "重听美式发音");

        // 单词结果头：首词直接置于左上角，紧邻音标胶囊与同排发音操作。
        _phoneticBadge.Child = _phonetic;
        var phoneticRow = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        phoneticRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        phoneticRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        phoneticRow.Children.Add(_phoneticBadge);
        phoneticRow.Children.Add(_speak);
        Grid.SetColumn(_speak, 1);

        _wordHeader.Children.Add(_heading);
        _wordHeader.Children.Add(phoneticRow);
        _wordHeader.Children.Add(_wordDivider);

        // 非单词状态头：译文标签、加载环或状态图标。
        _statusLine.MinHeight = 20;
        _statusLine.Children.Add(_loadingRing);
        _statusLine.Children.Add(_statusIcon);
        _statusLine.Children.Add(_statusTitle);
        _statusHeader.Children.Add(_statusLine);
        _statusHeader.Children.Add(_statusDivider);

        _headerHost.Children.Add(_wordHeader);
        _headerHost.Children.Add(_statusHeader);

        _scroll.Content = _body;
        _bodyCard.Margin = new Thickness(0, 8, 0, 0);
        _bodyCard.Child = _scroll;

        _actions.Children.Add(_notice);

        var contentHost = new Grid
        {
            Padding = new Thickness(16, 12, 16, 14),
        };
        contentHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentHost.Children.Add(_headerHost);
        contentHost.Children.Add(_bodyCard);
        contentHost.Children.Add(_actions);
        Grid.SetRow(_bodyCard, 1);
        Grid.SetRow(_actions, 2);

        _root.Children.Add(_titleDragArea);
        _root.Children.Add(contentHost);

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            _dismissFromUi();
        };
        _root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        _root.KeyboardAccelerators.Add(escape);
        _root.KeyDown += (_, key) =>
        {
            if (key.Key == VirtualKey.Escape)
            {
                key.Handled = true;
                _dismissFromUi();
            }
        };
        _titleDragArea.SizeChanged += (_, _) => _syncDragRegion();
        _root.Loaded += (_, _) => _fitAfterFirstLoad();
        return _root;
    }

    // 构造带扬声器图标和紧凑文字的发音按钮内容。
    private static StackPanel _buildSpeakContent()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new FontIcon { Glyph = "\uE767", FontSize = 12 });
        panel.Children.Add(new TextBlock
        {
            Text = "美式",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    // 保留顶部留白拖动，单词区域依旧可以选字。
    private void _updateDragRegion(bool isWordMode)
    {
        double leftOffset = 0;
        if (isWordMode)
        {
            _heading.Measure(new Windows.Foundation.Size(MaxCardWidth - HorizontalPadding, double.PositiveInfinity));
            leftOffset = Math.Min(MaxCardWidth, Math.Ceiling(_heading.DesiredSize.Width) + 20);
        }
        if (_titleDragArea.Margin.Left != leftOffset)
        {
            _root.LayoutUpdated -= _syncDragRegionAfterLayout;
            _root.LayoutUpdated += _syncDragRegionAfterLayout;
            _titleDragArea.Margin = new Thickness(leftOffset, 0, 0, 0);
        }
    }

    // 只同步一次新布局中的位置，覆盖拖动区移动但宽度未变的情况。
    private void _syncDragRegionAfterLayout(object? sender, object args)
    {
        _root.LayoutUpdated -= _syncDragRegionAfterLayout;
        _syncDragRegion();
    }

    // 将无标题栏卡片的拖动区映射为 Windows 的物理像素命中区域。
    private void _syncDragRegion()
    {
        if (_closing || _titleDragArea.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        _nonClientPointer ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        if (_titleDragArea.ActualWidth <= 0 || _titleDragArea.ActualHeight <= 0)
        {
            _nonClientPointer.ClearRegionRects(NonClientRegionKind.Caption);
            return;
        }

        var origin = _titleDragArea.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point());
        double scale = xamlRoot.RasterizationScale;
        _nonClientPointer.SetRegionRects(NonClientRegionKind.Caption,
        [
            new RectInt32(
                (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
                (int)Math.Round(_titleDragArea.ActualWidth * scale), (int)Math.Round(_titleDragArea.ActualHeight * scale)),
        ]);
    }

    // 按资源键安全读取应用样式，缺失时不抛异常。
    private static void _applyOptionalStyle(FrameworkElement element, string resourceKey)
    {
        if (_resource<Style>(resourceKey) is { } style)
        {
            element.Style = style;
        }
    }

    // 从应用资源字典读取指定类型的共享资源。
    private static T? _resource<T>(string key) where T : class =>
        Application.Current?.Resources.TryGetValue(key, out object? value) == true ? value as T : null;
}
