using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Views;

/// <summary>Animate only the camera marker between shared feedback frames. The Glass/Cell transforms remain fixed.</summary>
public partial class CReviewCameraOverlay : UserControl
{
    private CReviewCameraPreview? _camera;
    private int _projectionVersion = -1;
    private bool _wasVisible;
    public CReviewCameraOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        Loaded += (_, _) => { Subscribe(); UpdateMarker(animate: false); };
        Unloaded += (_, _) => { Unsubscribe(); StopAnimations(); _wasVisible = false; };
    }

    private void Subscribe()
    {
        if (!ReferenceEquals(_camera, DataContext))
        {
            Unsubscribe(); _camera = DataContext as CReviewCameraPreview;
            if (_camera is not null) _camera.PropertyChanged += CameraChanged;
        }
        UpdateMarker(animate: false);
    }
    private void Unsubscribe()
    {
        if (_camera is not null) _camera.PropertyChanged -= CameraChanged;
        _camera = null;
    }
    private void CameraChanged(object? sender, PropertyChangedEventArgs e) => UpdateMarker(animate: true);

    private void UpdateMarker(bool animate)
    {
        if (_camera is not { IsVisible: true })
        { CameraMarker.Visibility = Visibility.Collapsed; StopAnimations(); _wasVisible = false; return; }
        var smooth = animate && _wasVisible && _projectionVersion == _camera.ProjectionVersion;
        var x = _camera.Center.X - 18;
        var y = _camera.Center.Y - 14;
        if (smooth)
        {
            CameraTranslation.BeginAnimation(TranslateTransform.XProperty, Animation(CameraTranslation.X, x), HandoffBehavior.SnapshotAndReplace);
            CameraTranslation.BeginAnimation(TranslateTransform.YProperty, Animation(CameraTranslation.Y, y), HandoffBehavior.SnapshotAndReplace);
        }
        else
        { StopAnimations(); CameraTranslation.X = x; CameraTranslation.Y = y; }
        CameraMarker.Visibility = Visibility.Visible;
        _wasVisible = true; _projectionVersion = _camera.ProjectionVersion;
    }
    private void StopAnimations()
    { CameraTranslation.BeginAnimation(TranslateTransform.XProperty, null); CameraTranslation.BeginAnimation(TranslateTransform.YProperty, null); }
    private static DoubleAnimation Animation(double from, double to) => new(from, to, new Duration(TimeSpan.FromMilliseconds(180)));
}
