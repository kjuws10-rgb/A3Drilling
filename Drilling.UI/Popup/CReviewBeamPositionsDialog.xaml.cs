using System.Globalization;
using System.Windows;
using Drilling.Common.Review;

namespace Drilling.UI.Popup;

public partial class CReviewBeamPositionsDialog : Window
{
    public int GridSize { get; }

    public CReviewBeamPositionsDialog(ST_REVIEW_PLAN_POINT point, IReadOnlyList<ST_REVIEW_BEAM_POSITION> positions)
    {
        GridSize = (int)Math.Ceiling(Math.Sqrt(positions.Count));
        InitializeComponent();
        DataContext = this;
        var head = point.HeadNo > 0 ? $"HEAD {point.HeadNo:00}" : "HEAD 미할당";
        ShotSummaryText.Text = $"{head} · CELL {point.CellNo:00} · Shot {CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX)} · 목표 위치";
        BeamItems.ItemsSource = positions.Select(position => new ST_REVIEW_BEAM_POSITION_ITEM(position)).ToArray();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}

public sealed class ST_REVIEW_BEAM_POSITION_ITEM(ST_REVIEW_BEAM_POSITION position)
{
    public int BeamNo => position.BeamNo;
    public string StageYText => position.StageY.ToString("0.0000", CultureInfo.InvariantCulture);
    public string ReviewXText => position.ReviewX.ToString("0.0000", CultureInfo.InvariantCulture);
}
