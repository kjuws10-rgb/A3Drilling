using System.Windows;
using System.Windows.Data;

namespace Drilling.UI.Popup;

public partial class CSelectedReviewShotsDialog : Window
{
    public CSelectedReviewShotsDialog(IReadOnlyList<ST_REVIEW_SELECTED_SHOT_ROW> rows, int headCount)
    {
        InitializeComponent();
        var selectedHeads = rows.Select(row => row.HeadNo).ToHashSet();
        var displayRows = rows.Concat(Enumerable.Range(1, headCount)
                .Where(headNo => !selectedHeads.Contains(headNo))
                .Select(headNo => new ST_REVIEW_SELECTED_SHOT_ROW(headNo, 0, 0, "선택된 Shot 없음") { IsEmpty = true }))
            .OrderBy(row => row.HeadNo > 0 ? row.HeadNo : int.MaxValue)
            .ThenBy(row => row.CellNo)
            .ThenBy(row => row.ShotNo)
            .ToArray();
        var view = new ListCollectionView(displayRows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ST_REVIEW_SELECTED_SHOT_ROW.Head)));
        ShotGrid.ItemsSource = view;
        SummaryText.Text = $"선택한 Shot: {rows.Count:N0}개 · Head / Cell / Shot 순서";
        EmptyText.Visibility = displayRows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}

public sealed record ST_REVIEW_SELECTED_SHOT_ROW(int HeadNo, int CellNo, int ShotNo, string ShotName)
{
    public bool IsEmpty { get; init; }
    public string Head => HeadNo > 0 ? $"HEAD {HeadNo:00}" : "미할당";
    public string Cell => IsEmpty ? "-" : $"CELL {CellNo:00}";
}
