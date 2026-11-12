using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IdleSenior.UI.Helpers
{
    /// <summary>
    /// Ensures smooth vertical and horizontal mouse-wheel scrolling across all pages,
    /// preventing child controls from swallowing MouseWheel events while accurately finding the scrollable container.
    /// </summary>
    public static class ScrollBubbleHelper
    {
        public static readonly DependencyProperty EnableSmoothScrollProperty =
            DependencyProperty.RegisterAttached(
                "EnableSmoothScroll",
                typeof(bool),
                typeof(ScrollBubbleHelper),
                new PropertyMetadata(false, OnEnableSmoothScrollChanged));

        public static bool GetEnableSmoothScroll(DependencyObject obj) =>
            (bool)obj.GetValue(EnableSmoothScrollProperty);

        public static void SetEnableSmoothScroll(DependencyObject obj, bool value) =>
            obj.SetValue(EnableSmoothScrollProperty, value);

        public static readonly DependencyProperty HorizontalWheelScrollProperty =
            DependencyProperty.RegisterAttached(
                "HorizontalWheelScroll",
                typeof(bool),
                typeof(ScrollBubbleHelper),
                new PropertyMetadata(false, OnHorizontalWheelScrollChanged));

        public static bool GetHorizontalWheelScroll(DependencyObject obj) =>
            (bool)obj.GetValue(HorizontalWheelScrollProperty);

        public static void SetHorizontalWheelScroll(DependencyObject obj, bool value) =>
            obj.SetValue(HorizontalWheelScrollProperty, value);

        private static void OnEnableSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement element) return;

            element.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnVerticalPreviewMouseWheel));
            if (e.NewValue is true)
            {
                element.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnVerticalPreviewMouseWheel), handledEventsToo: true);
            }
        }

        private static void OnHorizontalWheelScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement element) return;

            element.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnHorizontalPreviewMouseWheel));
            if (e.NewValue is true)
            {
                element.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnHorizontalPreviewMouseWheel), handledEventsToo: true);
            }
        }

        private static void OnVerticalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.OriginalSource is DependencyObject origin && IsInsideComboBoxPopup(origin))
                return;

            // Shift + MouseWheel for horizontal scrolling
            if (Keyboard.Modifiers == ModifierKeys.Shift && e.OriginalSource is DependencyObject origH)
            {
                var horizSv = FindAncestorScrollViewer(origH, horizontalOnly: true);
                if (horizSv != null && horizSv.ScrollableWidth > 0)
                {
                    horizSv.ScrollToHorizontalOffset(horizSv.HorizontalOffset - e.Delta);
                    e.Handled = true;
                    return;
                }
            }

            // Find a ScrollViewer that actually has scrollable height and can scroll in this direction
            var targetSv = FindActiveScrollableTarget(e.OriginalSource as DependencyObject, sender as DependencyObject, e.Delta);
            if (targetSv == null) return;

            double step = e.Delta * 0.75;
            double current = targetSv.VerticalOffset;
            double next = Math.Clamp(current - step, 0, targetSv.ScrollableHeight);

            if (Math.Abs(next - current) > 0.01)
            {
                targetSv.ScrollToVerticalOffset(next);
                e.Handled = true;
            }
        }

        private static void OnHorizontalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            var parentVertical = FindParentScrollViewer(sv);
            bool parentCanScrollVertically = parentVertical != null && parentVertical.ScrollableHeight > 0;

            if (!parentCanScrollVertically || Keyboard.Modifiers == ModifierKeys.Shift)
            {
                if (sv.ScrollableWidth > 0)
                {
                    sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                    e.Handled = true;
                }
            }
            else if (parentVertical != null)
            {
                double step = e.Delta * 0.65;
                double nextOffset = Math.Clamp(parentVertical.VerticalOffset - step, 0, parentVertical.ScrollableHeight);
                if (Math.Abs(nextOffset - parentVertical.VerticalOffset) > 0.01)
                {
                    parentVertical.ScrollToVerticalOffset(nextOffset);
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// Obsolete: Page containers manage their own viewport layouts naturally.
        /// </summary>
        public static void DisableNavigationHostInternalScroll(DependencyObject navigationView)
        {
            // No-op to prevent disabling page scrollviewers
        }

        private static ScrollViewer? FindActiveScrollableTarget(DependencyObject? origin, DependencyObject? sender, int delta)
        {
            // First check upwards from the element directly under the cursor
            var current = origin;
            while (current != null)
            {
                if (current is ScrollViewer sv && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && sv.ScrollableHeight > 0)
                {
                    return sv;
                }
                current = GetParentSafe(current);
            }

            // Fallback: search from sender
            if (sender is ScrollViewer senderSv && senderSv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && senderSv.ScrollableHeight > 0)
            {
                return senderSv;
            }

            if (sender != null)
            {
                var descendant = FindScrollableDescendant(sender, delta);
                if (descendant != null) return descendant;
            }

            return null;
        }

        private static ScrollViewer? FindScrollableDescendant(DependencyObject root, int delta)
        {
            if (root is ScrollViewer sv && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && sv.ScrollableHeight > 0)
            {
                return sv;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = FindScrollableDescendant(child, delta);
                if (found != null) return found;
            }
            return null;
        }

        private static bool IsInsideComboBoxPopup(DependencyObject current)
        {
            var node = current;
            while (node != null)
            {
                if (node is ComboBoxItem) return true;
                node = GetParentSafe(node);
            }
            return false;
        }

        private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
        {
            var current = GetParentSafe(child);
            while (current != null)
            {
                if (current is ScrollViewer sv && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                    return sv;
                current = GetParentSafe(current);
            }
            return null;
        }

        private static ScrollViewer? FindAncestorScrollViewer(DependencyObject child, bool horizontalOnly)
        {
            var current = child;
            while (current != null)
            {
                if (current is ScrollViewer sv)
                {
                    if (!horizontalOnly || sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                        return sv;
                }
                current = GetParentSafe(current);
            }
            return null;
        }

        private static DependencyObject? GetParentSafe(DependencyObject? obj)
        {
            if (obj == null) return null;
            if (obj is Visual || obj is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(obj);
            return LogicalTreeHelper.GetParent(obj);
        }
    }
}
