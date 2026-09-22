using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Windows.Native;

namespace DictationApp.Overlay;

/// <summary>
/// Bottom-centre overlay that never takes keyboard focus (WS_EX_NOACTIVATE). Three modes: the full bar
/// (state, live text, chips), a minimal pill showing only the microphone level, or hidden. The full bar
/// lingers briefly after a dictation so the user can read "Nothing heard" or "cleanup skipped".
/// </summary>
public partial class FlowBarWindow : Window
{
    private static readonly TimeSpan LingerAfterIdle = TimeSpan.FromMilliseconds(1800);

    private readonly FlowBarViewModel _viewModel;
    private readonly DictationStatusHub _hub;
    private readonly ISettingsStore _settings;
    private readonly DispatcherTimer _hideTimer;
    private nint _targetWindow;
    private bool _attached;
    private FlowBarMode _appliedMode = FlowBarMode.Full;

    public FlowBarWindow(FlowBarViewModel viewModel, DictationStatusHub hub, ISettingsStore settings)
    {
        _viewModel = viewModel;
        _hub = hub;
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = LingerAfterIdle };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _hub.Changed += status => Dispatcher.BeginInvoke(() => OnStatus(status));
        _hub.Flashed += () => Dispatcher.BeginInvoke(Flash);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowHelper.MakeNoActivateToolWindow(hwnd);
    }

    private void OnStatus(DictationStatus status)
    {
        _viewModel.Apply(status);
        var mode = _settings.Current.FlowBarMode;
        if (mode == FlowBarMode.Hidden)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        ApplyMode(mode);
        if (status.IsActive)
        {
            _hideTimer.Stop();
            if (status.State == DictationState.Arming)
            {
                _targetWindow = WindowHelper.GetForegroundWindow();
            }

            Reposition();
            if (!IsVisible)
            {
                Show();
            }

            AnimateDot(status.State == DictationState.Recording);
            AnimateMini(status.State is DictationState.Finalising or DictationState.PostProcessing or DictationState.Inserting);
        }
        else
        {
            AnimateDot(false);
            AnimateMini(false);
            if (IsVisible)
            {
                // The minimal pill has nothing to say once idle; the full bar lingers to show its badge.
                if (mode == FlowBarMode.Minimal || string.IsNullOrEmpty(status.Badge))
                {
                    Hide();
                }
                else
                {
                    _hideTimer.Stop();
                    _hideTimer.Start();
                }
            }
        }
    }

    private void ApplyMode(FlowBarMode mode)
    {
        if (mode == _appliedMode && IsLoaded)
        {
            return;
        }

        _appliedMode = mode;
        var minimal = mode == FlowBarMode.Minimal;
        Root.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        MiniRoot.Visibility = minimal ? Visibility.Visible : Visibility.Collapsed;
        InvalidateMeasure();
    }

    private void Reposition()
    {
        var anchor = _targetWindow != 0 && WindowHelper.IsWindow(_targetWindow) ? _targetWindow : WindowHelper.GetForegroundWindow();
        var (left, _, right, bottom) = WindowHelper.GetWorkArea(anchor);
        var scale = WindowHelper.GetDpiScale(anchor);
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = DesiredSize.Width > 0 ? DesiredSize.Width : ActualWidth;
        var height = DesiredSize.Height > 0 ? DesiredSize.Height : ActualHeight;
        var centreX = (left + right) / 2.0 / scale;
        Left = centreX - width / 2;
        Top = bottom / scale - height - (_appliedMode == FlowBarMode.Minimal ? 6 : 12);
    }

    private void AnimateDot(bool pulse)
    {
        if (pulse)
        {
            var anim = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Dot.BeginAnimation(OpacityProperty, anim);
            Dot.Fill = (Brush)FindResource("RecordingBrush");
        }
        else
        {
            Dot.BeginAnimation(OpacityProperty, null);
            Dot.Opacity = 1.0;
            Dot.Fill = (Brush)FindResource("AccentBrush");
        }
    }

    /// <summary>While the text is being finished and inserted the tiny bar has no audio to show, so it breathes instead.</summary>
    private void AnimateMini(bool busy)
    {
        if (busy)
        {
            var anim = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            MiniFill.BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            MiniFill.BeginAnimation(OpacityProperty, null);
            MiniFill.Opacity = 1.0;
        }
    }

    private void Flash()
    {
        if (!IsVisible)
        {
            return;
        }

        var target = _appliedMode == FlowBarMode.Minimal ? MiniRoot : Root;
        var brush = new SolidColorBrush(Colors.White);
        target.BorderBrush = brush;
        var anim = new ColorAnimation(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(400));
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
