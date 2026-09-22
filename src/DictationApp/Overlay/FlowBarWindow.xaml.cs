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
/// Bottom-centre overlay that never takes keyboard focus (WS_EX_NOACTIVATE). It appears when a dictation
/// starts and lingers briefly after it ends so the user can read "Nothing heard" or "cleanup skipped".
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
        if (!_settings.Current.ShowFlowBar)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (status.IsActive)
        {
            _hideTimer.Stop();
            if (status.State == DictationState.Arming)
            {
                _targetWindow = WindowHelper.GetForegroundWindow();
            }

            if (!IsVisible)
            {
                Reposition();
                Show();
            }
            else
            {
                Reposition();
            }

            AnimateDot(status.State == DictationState.Recording);
        }
        else
        {
            AnimateDot(false);
            if (IsVisible)
            {
                if (string.IsNullOrEmpty(status.Badge))
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

    private void Reposition()
    {
        var anchor = _targetWindow != 0 && WindowHelper.IsWindow(_targetWindow) ? _targetWindow : WindowHelper.GetForegroundWindow();
        var (left, top, right, bottom) = WindowHelper.GetWorkArea(anchor);
        var scale = WindowHelper.GetDpiScale(anchor);
        // Measure first so ActualWidth is valid before the first Show.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        var height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        var centreX = (left + right) / 2.0 / scale;
        Left = centreX - width / 2;
        Top = bottom / scale - height - 12;
        _ = top; // work area top is not needed for bottom anchoring
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

    private void Flash()
    {
        if (!IsVisible)
        {
            return;
        }

        var brush = new SolidColorBrush(Colors.White);
        Root.BorderBrush = brush;
        var anim = new ColorAnimation(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(400));
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
