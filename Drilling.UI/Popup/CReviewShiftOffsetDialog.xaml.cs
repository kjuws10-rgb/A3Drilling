using System.Globalization;
using System.Windows;
using Drilling.Common.Station;

namespace Drilling.UI.Popup;

public enum EN_REVIEW_SHIFT_OFFSET_CHOICE
{
    Cancel,
    Apply,
    WithoutShift
}

public partial class CReviewShiftOffsetDialog : Window
{
    private EN_REVIEW_SHIFT_OFFSET_CHOICE _choice = EN_REVIEW_SHIFT_OFFSET_CHOICE.Cancel;

    private CReviewShiftOffsetDialog(ST_MANUAL_MOF_COMPLETION completion, string context)
    {
        InitializeComponent();
        ContextText.Text = context;
        CompletedAtText.Text = completion.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        ShiftXText.Text = $"{completion.ShiftOffsetX:+0.000000;-0.000000;0.000000} mm";
        ShiftYText.Text = $"{completion.ShiftOffsetY:+0.000000;-0.000000;0.000000} mm";
    }

    public static EN_REVIEW_SHIFT_OFFSET_CHOICE Show(ST_MANUAL_MOF_COMPLETION completion, string context)
    {
        var dialog = new CReviewShiftOffsetDialog(completion, context)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        return dialog._choice;
    }

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        _choice = EN_REVIEW_SHIFT_OFFSET_CHOICE.Apply;
        DialogResult = true;
    }

    private void OnWithoutShiftClicked(object sender, RoutedEventArgs e)
    {
        _choice = EN_REVIEW_SHIFT_OFFSET_CHOICE.WithoutShift;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _choice = EN_REVIEW_SHIFT_OFFSET_CHOICE.Cancel;
        DialogResult = false;
    }
}
