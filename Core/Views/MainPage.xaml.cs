using System;
using System.Collections.Generic;
using System.Linq;
using AudioMixerWin.Core.Controls;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace AudioMixerWin.Core.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    private class DragState
    {
        public int DraggedIndex { get; set; } = -1;
        public double StartScreenY { get; set; }
        public bool IsActive { get; set; }
        public const int DragThreshold = 15;
    }

    private readonly DragState _dragState = new();
    private FrameworkElement? _draggedElement;

    public MainPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    internal void OnKnobCardPointerPressed(KnobCard card, PointerRoutedEventArgs e)
    {
        try
        {
            if (_dragState.DraggedIndex >= 0)
                return;

            var index = GetChannelIndexForCard(card);
            if (index < 0)
                return;

            _dragState.DraggedIndex = index;
            _dragState.StartScreenY = e.GetCurrentPoint(null).Position.Y;
            _dragState.IsActive = false;
            _draggedElement = GetCardElementAtIndex(index);

            if (e.OriginalSource is UIElement element)
                element.CapturePointer(e.Pointer);

            e.Handled = true;
        }
        catch
        {
            ResetDragState();
        }
    }

    internal void OnKnobCardPointerMoved(KnobCard card, PointerRoutedEventArgs e)
    {
        try
        {
            if (_dragState.DraggedIndex < 0)
                return;

            var currentY = e.GetCurrentPoint(null).Position.Y;
            var delta = Math.Abs(currentY - _dragState.StartScreenY);

            if (!_dragState.IsActive && delta >= DragState.DragThreshold)
            {
                _dragState.IsActive = true;
                ViewModel.IsDragging = true;
                ViewModel.DraggedChannelIndex = _dragState.DraggedIndex;
                ApplyDragVisuals(_draggedElement);
            }

            if (!_dragState.IsActive)
                return;

            var targetIndex = HitTestDropTarget(currentY);
            ViewModel.TargetDropIndex = targetIndex;

            if (targetIndex >= 0 && targetIndex < ViewModel.Channels.Count)
            {
                var cardElement = GetCardElementAtIndex(targetIndex);
                if (cardElement != null)
                {
                    var position = cardElement.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
                    ViewModel.DragDropIndicatorY = position.Y;
                }
            }

            e.Handled = true;
        }
        catch
        {
            ResetDragState();
        }
    }

    internal void OnKnobCardPointerReleased(KnobCard card, PointerRoutedEventArgs e)
    {
        try
        {
            if (_dragState.DraggedIndex < 0)
                return;

            var targetIndex = ViewModel.TargetDropIndex;
            var fromIndex = _dragState.DraggedIndex;

            if (e.OriginalSource is UIElement element)
                element.ReleasePointerCapture(e.Pointer);

            if (_dragState.IsActive && targetIndex >= 0 && targetIndex != fromIndex)
            {
                ViewModel.ReorderChannels(fromIndex, targetIndex);
            }

            ClearDragVisuals(_draggedElement);
            ClearDragState();
            e.Handled = true;
        }
        catch
        {
            ResetDragState();
        }
    }

    internal void OnKnobCardPointerCaptureLost(KnobCard card, PointerRoutedEventArgs e)
    {
        try
        {
            if (ViewModel.IsDragging)
            {
                ResetDragState();
            }
        }
        catch
        {
            ResetDragState();
        }
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.IsDragging)
        {
            ResetDragState();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private int GetChannelIndexForCard(KnobCard card)
    {
        if (card?.Channel is null)
            return -1;

        return ViewModel.Channels.IndexOf(card.Channel);
    }

    private int HitTestDropTarget(double screenY)
    {
        if (ChannelsRepeater is null)
            return -1;

        int closestIndex = -1;
        double closestDistance = double.MaxValue;

        for (int i = 0; i < ViewModel.Channels.Count; i++)
        {
            if (i == ViewModel.DraggedChannelIndex)
                continue;

            var container = ChannelsRepeater.TryGetElement(i) as FrameworkElement;
            if (container is null)
                continue;

            var position = container.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, container.ActualHeight / 2));
            var distance = Math.Abs(position.Y - screenY);

            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestIndex = i;
            }
        }

        return closestIndex;
    }

    private FrameworkElement? GetCardElementAtIndex(int index)
    {
        return ChannelsRepeater?.TryGetElement(index) as FrameworkElement;
    }

    private void ClearDragState()
    {
        ViewModel.IsDragging = false;
        ViewModel.DraggedChannelIndex = -1;
        ViewModel.TargetDropIndex = -1;
        ViewModel.DragDropIndicatorY = -1;
        _dragState.DraggedIndex = -1;
        _dragState.IsActive = false;
        _draggedElement = null;
    }

    private void ResetDragState()
    {
        ViewModel.IsDragging = false;
        ViewModel.DraggedChannelIndex = -1;
        ViewModel.TargetDropIndex = -1;
        ViewModel.DragDropIndicatorY = -1;
        _dragState.DraggedIndex = -1;
        _dragState.IsActive = false;
        _draggedElement = null;
    }

    private void ApplyDragVisuals(FrameworkElement? element)
    {
        if (element == null)
            return;

        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var transform = element.RenderTransform as CompositeTransform ?? new CompositeTransform();
        element.RenderTransform = transform;

        var storyboard = new Storyboard();

        var opacityAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.5,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacityAnim, element);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        storyboard.Children.Add(opacityAnim);

        var scaleXAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.95,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleXAnim, transform);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
        storyboard.Children.Add(scaleXAnim);

        var scaleYAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.95,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleYAnim, transform);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
        storyboard.Children.Add(scaleYAnim);

        storyboard.Begin();
    }

    private void ClearDragVisuals(FrameworkElement? element)
    {
        if (element == null)
            return;

        if (element.RenderTransform is not CompositeTransform transform)
            return;

        var storyboard = new Storyboard();

        var opacityAnim = new DoubleAnimation
        {
            From = 0.5,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacityAnim, element);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        storyboard.Children.Add(opacityAnim);

        var scaleXAnim = new DoubleAnimation
        {
            From = 0.95,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleXAnim, transform);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
        storyboard.Children.Add(scaleXAnim);

        var scaleYAnim = new DoubleAnimation
        {
            From = 0.95,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleYAnim, transform);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
        storyboard.Children.Add(scaleYAnim);

        storyboard.Begin();
    }

}
