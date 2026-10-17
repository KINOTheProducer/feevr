using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace Tfmm.App.Views;

/// <summary>The J mark. Set <see cref="Loading"/> and a top-down train runs along the J from the top station
/// to the bottom one, over and over; when it goes false the train finishes its run and the mark goes still.</summary>
public partial class Logo : UserControl
{
    public static readonly StyledProperty<bool> LoadingProperty = AvaloniaProperty.Register<Logo, bool>(nameof(Loading));
    public bool Loading { get => GetValue(LoadingProperty); set => SetValue(LoadingProperty, value); }

    // The route in Logo.axaml, as pieces in order: lines (x1, y1, x2, y2) and arcs (cx, cy, r, from°, to°; y points down).
    static readonly Piece[] Route =
    [
        Piece.Line(33, 15, 39, 15),
        Piece.Arc(39, 21, 6, -90, 0),
        Piece.Line(45, 21, 45, 37),
        Piece.Arc(32, 37, 13, 0, 180),
    ];
    static readonly double RouteLength = Route.Sum(p => p.Length);

    const int Cars = 3;
    const double CarLength = 6, CarWidth = 5, Gap = 1.5;
    const double TrainLength = Cars * CarLength + (Cars - 1) * Gap;
    const double Speed = 65;  // grid units per second, about 1.4 s a run

    readonly Rectangle[] _cars = new Rectangle[Cars];
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly System.Diagnostics.Stopwatch _run = new();

    public Logo()
    {
        InitializeComponent();
        for (var i = 0; i < Cars; i++)
        {
            _cars[i] = new Rectangle
            {
                Width = CarLength, Height = CarWidth, RadiusX = 1.4, RadiusY = 1.4, IsVisible = false,
                Fill = (IBrush)Application.Current!.FindResource(i == 0 ? "Text" : "Text2")!,
                RenderTransform = new RotateTransform(),
            };
            Board.Children.Add(_cars[i]);
        }
        _timer.Tick += (_, _) =>
        {
            if (Draw(_run.Elapsed.TotalSeconds)) return;
            if (Loading) _run.Restart();  // next run
            else Stop();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LoadingProperty && Loading && !_timer.IsEnabled && VisualRoot is not null) Start();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Loading && !_timer.IsEnabled) Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
    }

    void Start()
    {
        _run.Restart();
        _timer.Start();
    }

    void Stop()
    {
        _timer.Stop();
        HideCars();
    }

    /// <summary>Places the train <paramref name="seconds"/> into a run. False once it has left the track.</summary>
    public bool Draw(double seconds)
    {
        var head = seconds * Speed;
        for (var i = 0; i < Cars; i++)
        {
            var mid = head - CarLength / 2 - i * (CarLength + Gap);
            var car = _cars[i];
            // Only on the track: cars enter at the top station and leave at the bottom one.
            car.IsVisible = mid >= CarLength / 2 && mid <= RouteLength - CarLength / 2;
            if (!car.IsVisible) continue;
            var (x, y, angle) = At(mid);
            Canvas.SetLeft(car, x - CarLength / 2);
            Canvas.SetTop(car, y - CarWidth / 2);
            ((RotateTransform)car.RenderTransform!).Angle = angle;
        }
        return head <= RouteLength + TrainLength;
    }

    void HideCars()
    {
        foreach (var c in _cars) c.IsVisible = false;
    }

    /// <summary>Point and heading (degrees) at distance <paramref name="s"/> along the route.</summary>
    static (double X, double Y, double Angle) At(double s)
    {
        foreach (var p in Route)
        {
            if (s <= p.Length) return p.At(s);
            s -= p.Length;
        }
        return Route[^1].At(Route[^1].Length);
    }

    readonly record struct Piece(bool IsArc, double A, double B, double C, double D, double E)
    {
        public static Piece Line(double x1, double y1, double x2, double y2) => new(false, x1, y1, x2, y2, 0);
        public static Piece Arc(double cx, double cy, double r, double from, double to) => new(true, cx, cy, r, from, to);

        public double Length => IsArc ? C * Math.Abs(E - D) * Math.PI / 180 : Math.Sqrt((C - A) * (C - A) + (D - B) * (D - B));

        public (double, double, double) At(double s)
        {
            if (!IsArc)
            {
                var f = Length == 0 ? 0 : s / Length;
                return (A + (C - A) * f, B + (D - B) * f, Math.Atan2(D - B, C - A) * 180 / Math.PI);
            }
            var deg = D + Math.Sign(E - D) * s / C * 180 / Math.PI;
            var rad = deg * Math.PI / 180;
            return (A + C * Math.Cos(rad), B + C * Math.Sin(rad), deg + Math.Sign(E - D) * 90);
        }
    }
}
