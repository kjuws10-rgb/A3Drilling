namespace Drilling.UI.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drilling.UI.Menu.Menus;

/// <summary>
/// Draws the Review Shot matrix as one visual instead of materializing hundreds of buttons.
/// </summary>
public sealed class CReviewShotMapControl : FrameworkElement
{
    private const double CellSize = 18.0;
    private const double CellMargin = 0.5;
    private const double CellPitch = CellSize + (CellMargin * 2.0);

    private static readonly Brush BeamSelectionBackground = FrozenBrush(0x25, 0x63, 0xEB);
    private static readonly Brush BeamSelectionBorder = FrozenBrush(0x1E, 0x3A, 0x8A);
    private static readonly Brush CurrentBackground = FrozenBrush(0x35, 0xC7, 0x7A);
    private static readonly Brush CurrentBorder = FrozenBrush(0x13, 0x7A, 0x47);
    private static readonly Brush MaskingBackground = FrozenBrush(0xEF, 0x44, 0x44);
    private static readonly Brush MaskingBorder = FrozenBrush(0xB9, 0x1C, 0x1C);
    private static readonly Brush ActiveSelectionBorder = FrozenBrush(0xF5, 0x9E, 0x0B);
    private static readonly Brush SelectionMarkerBackground = FrozenBrush(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Brush SelectionMarkerForeground = FrozenBrush(0x92, 0x40, 0x0E);
    private static readonly Brush White = Brushes.White;

    private readonly Dictionary<Brush, Dictionary<double, Pen>> _pens = [];
    private readonly DispatcherTimer _blinkTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };
    private readonly ToolTip _toolTip;
    private readonly TextBlock _toolTipTitle;
    private readonly TextBlock _toolTipDetail;
    private bool _blinkVisible = true;
    private bool _rowsAttached;
    private ST_REVIEW_RUN_SHOT_ROW? _hoveredShot;

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows),
        typeof(IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW>),
        typeof(CReviewShotMapControl),
        new FrameworkPropertyMetadata(
            Array.Empty<ST_REVIEW_RUN_SHOT_MATRIX_ROW>(),
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnRowsChanged));

    public static readonly DependencyProperty IsBeamInspectionModeProperty = DependencyProperty.Register(
        nameof(IsBeamInspectionMode),
        typeof(bool),
        typeof(CReviewShotMapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public CReviewShotMapControl()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;

        _toolTipTitle = new TextBlock { FontWeight = FontWeights.Bold };
        _toolTipDetail = new TextBlock { Margin = new Thickness(0, 2, 0, 0) };
        _toolTip = new ToolTip
        {
            Content = new StackPanel
            {
                Children =
                {
                    _toolTipTitle,
                    _toolTipDetail
                }
            }
        };
        ToolTip = _toolTip;
        ToolTipService.SetInitialShowDelay(this, 350);
        ToolTipService.SetShowDuration(this, 15000);

        _blinkTimer.Tick += BlinkTimer_Tick;
        Loaded += (_, _) =>
        {
            if (!_rowsAttached)
            {
                AttachRows(Rows);
                _rowsAttached = true;
            }

            _blinkTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _blinkTimer.Stop();
            if (_rowsAttached)
            {
                DetachRows(Rows);
                _rowsAttached = false;
            }
        };
    }

    public IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW> Rows
    {
        get => (IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW>)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public bool IsBeamInspectionMode
    {
        get => (bool)GetValue(IsBeamInspectionModeProperty);
        set => SetValue(IsBeamInspectionModeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rowCount = Rows.Count;
        var columnCount = rowCount == 0 ? 0 : Rows.Max(row => row.Shots.Count);
        return new Size(columnCount * CellPitch, rowCount * CellPitch);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        for (var rowIndex = 0; rowIndex < Rows.Count; rowIndex++)
        {
            var shots = Rows[rowIndex].Shots;
            for (var columnIndex = 0; columnIndex < shots.Count; columnIndex++)
            {
                DrawShot(drawingContext, shots[columnIndex], rowIndex, columnIndex);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        var shot = HitTestShot(eventArgs.GetPosition(this));
        if (ReferenceEquals(_hoveredShot, shot))
            return;

        _hoveredShot = shot;
        _toolTipTitle.Text = shot?.ShotName ?? "";
        _toolTipDetail.Text = shot?.Detail ?? "";
        if (shot is null)
            _toolTip.IsOpen = false;
    }

    protected override void OnMouseLeave(MouseEventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        _hoveredShot = null;
        _toolTip.IsOpen = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonDown(eventArgs);
        var shot = HitTestShot(eventArgs.GetPosition(this));
        if (shot is null || !shot.SelectCommand.CanExecute(shot.ShotKey))
            return;

        shot.SelectCommand.Execute(shot.ShotKey);
        eventArgs.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseRightButtonDown(eventArgs);
        var shot = HitTestShot(eventArgs.GetPosition(this));
        var command = shot?.ShowBeamPositionsCommand;
        if (shot is null || command is null || !command.CanExecute(shot.ShotKey))
            return;

        command.Execute(shot.ShotKey);
        eventArgs.Handled = true;
    }

    private static void OnRowsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var control = (CReviewShotMapControl)dependencyObject;
        if (control._rowsAttached)
        {
            control.DetachRows(eventArgs.OldValue as IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW>);
            control.AttachRows(eventArgs.NewValue as IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW>);
        }
        control._hoveredShot = null;
        control.InvalidateMeasure();
        control.InvalidateVisual();
    }

    private void AttachRows(IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW>? rows)
    {
        if (rows is null)
            return;

        foreach (var shot in rows.SelectMany(row => row.Shots))
        {
            shot.BackgroundBrushChanged += Shot_VisualChanged;
            shot.BorderBrushChanged += Shot_VisualChanged;
            shot.DetailChanged += Shot_VisualChanged;
        }
    }

    private void DetachRows(IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW>? rows)
    {
        if (rows is null)
            return;

        foreach (var shot in rows.SelectMany(row => row.Shots))
        {
            shot.BackgroundBrushChanged -= Shot_VisualChanged;
            shot.BorderBrushChanged -= Shot_VisualChanged;
            shot.DetailChanged -= Shot_VisualChanged;
        }
    }

    private void Shot_VisualChanged(object? sender, EventArgs eventArgs)
    {
        if (Dispatcher.CheckAccess())
        {
            if (ReferenceEquals(_hoveredShot, sender) && sender is ST_REVIEW_RUN_SHOT_ROW shot)
                _toolTipDetail.Text = shot.Detail;
            InvalidateVisual();
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, InvalidateVisual);
    }

    private void BlinkTimer_Tick(object? sender, EventArgs eventArgs)
    {
        if (!Rows.SelectMany(row => row.Shots).Any(shot => shot.IsCurrent))
        {
            _blinkVisible = true;
            return;
        }

        _blinkVisible = !_blinkVisible;
        InvalidateVisual();
    }

    private void DrawShot(DrawingContext drawingContext, ST_REVIEW_RUN_SHOT_ROW shot, int row, int column)
    {
        var rect = new Rect(
            column * CellPitch + CellMargin,
            row * CellPitch + CellMargin,
            CellSize,
            CellSize);

        var background = shot.BackgroundBrush;
        var border = shot.BorderBrush;
        var borderThickness = shot.HasResult ? 1.2 : 0.7;

        var isMasking = shot.IsExcluded && !shot.ExclusionReason.Equals("CHESS", StringComparison.OrdinalIgnoreCase);
        if (shot.IsExcluded)
        {
            borderThickness = isMasking ? 1.6 : 1.2;
            if (isMasking)
            {
                background = MaskingBackground;
                border = MaskingBorder;
            }
        }

        if (shot.IsSelected)
        {
            borderThickness = 2.0;
            if (IsBeamInspectionMode)
            {
                background = BeamSelectionBackground;
                border = BeamSelectionBorder;
            }
        }

        if (shot.IsActiveSelection)
        {
            border = ActiveSelectionBorder;
            borderThickness = 3.0;
        }

        if (shot.IsCurrent)
        {
            background = CurrentBackground;
            border = CurrentBorder;
            borderThickness = 2.0;
            drawingContext.PushOpacity(_blinkVisible ? 1.0 : 0.38);
        }

        drawingContext.DrawRectangle(background, GetPen(border, borderThickness), rect);

        if (isMasking)
            DrawCross(drawingContext, rect, White, 2.4, 3.0);

        if (shot.IsActiveSelection || (shot.IsSelected && !IsBeamInspectionMode))
            DrawSelectionMarker(drawingContext, rect);

        if (shot.IsCurrent)
            drawingContext.Pop();
    }

    private void DrawSelectionMarker(DrawingContext drawingContext, Rect cellRect)
    {
        const double markerSize = 12.0;
        var marker = new Rect(
            cellRect.Left + ((cellRect.Width - markerSize) / 2.0),
            cellRect.Top + ((cellRect.Height - markerSize) / 2.0),
            markerSize,
            markerSize);
        drawingContext.DrawRectangle(SelectionMarkerBackground, GetPen(ActiveSelectionBorder, 2.0), marker);

        var pen = GetPen(SelectionMarkerForeground, 1.4);
        drawingContext.DrawLine(pen, new Point(marker.Left + 3.0, marker.Top + 4.5), new Point(marker.Left + 5.4, marker.Bottom - 3.0));
        drawingContext.DrawLine(pen, new Point(marker.Left + 5.4, marker.Bottom - 3.0), new Point(marker.Right - 2.5, marker.Top + 2.5));
    }

    private void DrawCross(DrawingContext drawingContext, Rect rect, Brush brush, double thickness, double inset)
    {
        var pen = GetPen(brush, thickness);
        drawingContext.DrawLine(pen, new Point(rect.Left + inset, rect.Top + inset), new Point(rect.Right - inset, rect.Bottom - inset));
        drawingContext.DrawLine(pen, new Point(rect.Right - inset, rect.Top + inset), new Point(rect.Left + inset, rect.Bottom - inset));
    }

    private ST_REVIEW_RUN_SHOT_ROW? HitTestShot(Point point)
    {
        if (point.X < 0 || point.Y < 0)
            return null;

        var rowIndex = (int)(point.Y / CellPitch);
        var columnIndex = (int)(point.X / CellPitch);
        if (rowIndex < 0 || rowIndex >= Rows.Count)
            return null;

        var shots = Rows[rowIndex].Shots;
        return columnIndex >= 0 && columnIndex < shots.Count ? shots[columnIndex] : null;
    }

    private Pen GetPen(Brush brush, double thickness)
    {
        if (!_pens.TryGetValue(brush, out var byThickness))
        {
            byThickness = [];
            _pens.Add(brush, byThickness);
        }

        if (byThickness.TryGetValue(thickness, out var pen))
            return pen;

        pen = new Pen(brush, thickness);
        if (pen.CanFreeze)
            pen.Freeze();
        byThickness.Add(thickness, pen);
        return pen;
    }

    private static Brush FrozenBrush(byte red, byte green, byte blue) => FrozenBrush(0xFF, red, green, blue);

    private static Brush FrozenBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}
