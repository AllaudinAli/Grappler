using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace Grappler.Core
{
    /// <summary>
    /// Eases a ProgressBar between the values it is given.
    ///
    /// Progress reaches the UI in 100ms steps (see DownloadItem), which
    /// without this reads as a bar ratcheting forward ten times a second.
    /// Binding Target instead of Value hands each step to a short animation
    /// so the bar glides. The cost is one animation clock per *visible*
    /// row - virtualisation means off-screen rows have no bar at all.
    ///
    /// Backwards jumps are snapped, never animated: a high-quality download
    /// runs the video stream to 100% and then restarts at 0 for the audio
    /// stream, and sliding smoothly backwards would look like a fault.
    /// </summary>
    public static class SmoothProgress
    {
        private static readonly Duration Glide =
            new Duration(TimeSpan.FromMilliseconds(160));

        private static readonly IEasingFunction Ease =
            MakeEase();

        private static IEasingFunction MakeEase()
        {
            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseOut;
            ease.Freeze();
            return ease;
        }

        public static readonly DependencyProperty TargetProperty =
            DependencyProperty.RegisterAttached(
                "Target", typeof(double), typeof(SmoothProgress),
                new PropertyMetadata(0.0, OnTargetChanged));

        public static void SetTarget(DependencyObject element, double value)
        {
            element.SetValue(TargetProperty, value);
        }

        public static double GetTarget(DependencyObject element)
        {
            return (double)element.GetValue(TargetProperty);
        }

        private static void OnTargetChanged(
            DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ProgressBar bar = d as ProgressBar;
            if (bar == null) { return; }

            double to = (double)e.NewValue;
            double from = bar.Value;

            if (to < from || Math.Abs(to - from) < 0.05)
            {
                // Clear the running clock first, or it would keep control of
                // the property and quietly ignore the assignment.
                bar.BeginAnimation(RangeBase.ValueProperty, null);
                bar.Value = to;
                return;
            }

            DoubleAnimation glide = new DoubleAnimation(to, Glide);
            glide.EasingFunction = Ease;
            bar.BeginAnimation(RangeBase.ValueProperty, glide);
        }
    }
}
