using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Drilling.UI.Popup;
public enum EN_DEFAULT_RESTART_CHOICE { Cancel, AllFromStart, InspectionFromStart }

public partial class CRecipeConfirmDialog : Window
{
    private bool _alternateSelected;
    public static bool ShowConfirmation(string title, string message, string confirmText = "확인", bool useDangerStyle = false)
    {
        var dialog = new CRecipeConfirmDialog(title, message, confirmText, useDangerStyle)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true;
    }

    public static bool ShowDuplicateOffsetConfirmation(string title, string message)
    {
        var dialog = new CRecipeConfirmDialog(title, message, "중복 적용", useDangerStyle: true)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.CancelButton.Content = "아니오";
        return dialog.ShowDialog() == true;
    }

    public static bool ShowSavedResultLoadConfirmation(string title, string message)
    {
        var dialog = new CRecipeConfirmDialog(title, message, "예, LOAD", useDangerStyle: false)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.CancelButton.Content = "아니오";
        return dialog.ShowDialog() == true;
    }

    public static bool ShowRecipeFolderCopy(string title, string message)
    {
        var dialog = new CRecipeConfirmDialog(title, message, "복사", useDangerStyle: false)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.CancelButton.Content = "복사 안 함";
        return dialog.ShowDialog() == true;
    }

    // true: rename this folder, false: keep it, null: cancel the entire recipe rename.
    public static bool? ShowRecipeFolderRename(string title, string message)
    {
        var dialog = CreateRecipeFolderRename(title, message);
        return dialog.ShowDialog() == true ? true : dialog._alternateSelected ? false : null;
    }

    internal static CRecipeConfirmDialog CreateRecipeFolderRename(string title, string message)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new CRecipeConfirmDialog(title, message, "함께 변경", useDangerStyle: false);
        if (owner is not null)
            dialog.Owner = owner;
        dialog.CancelButton.Content = "폴더 유지";
        dialog.AbortGap.Width = new GridLength(12);
        dialog.AbortColumn.Width = new GridLength(1, GridUnitType.Star);
        dialog.AbortButton.Visibility = Visibility.Visible;
        return dialog;
    }

    public static bool ShowRecipeFolderDelete(string title, string message) => ShowRecipeFolderAction(title, message, "함께 삭제", true);
    private static bool ShowRecipeFolderAction(string title, string message, string action, bool danger)
    {
        var dialog = new CRecipeConfirmDialog(title, message, action, danger)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.CancelButton.Content = "폴더 유지";
        return dialog.ShowDialog() == true;
    }

    public static void ShowCompletion(string title, string message, Window? owner = null)
    {
        ShowMessage(title, message, useDangerStyle: false, owner);
    }

    public static void ShowWarning(string title, string message, Window? owner = null)
    {
        ShowMessage(title, message, useDangerStyle: true, owner);
    }

    public static EN_REVIEW_RESUME_CHOICE ShowReviewResumeChoice(string message)
    {
        var dialog = new CRecipeConfirmDialog("미완료 Review 확인", message, "이어서 검사", useDangerStyle: false)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.CancelButton.Content = "처음부터";
        var result = dialog.ShowDialog();
        if (result == true)
        {
            return EN_REVIEW_RESUME_CHOICE.Resume;
        }

        return dialog._alternateSelected ? EN_REVIEW_RESUME_CHOICE.Restart : EN_REVIEW_RESUME_CHOICE.Cancel;
    }

    public static EN_REVIEW_RESUME_CHOICE ShowFineRetryChoice(string message)
    {
        var dialog = CreateFineRetryChoice(message);
        var result = dialog.ShowDialog();
        return result == true ? EN_REVIEW_RESUME_CHOICE.Resume : dialog._alternateSelected ? EN_REVIEW_RESUME_CHOICE.Restart : EN_REVIEW_RESUME_CHOICE.Cancel;
    }

    public static EN_DEFAULT_RESTART_CHOICE ShowDefaultRestartChoice(string message)
    {
        var dialog = CreateDefaultRestartChoice(message);
        var result = dialog.ShowDialog();
        return result == true ? EN_DEFAULT_RESTART_CHOICE.InspectionFromStart : dialog._alternateSelected ? EN_DEFAULT_RESTART_CHOICE.AllFromStart : EN_DEFAULT_RESTART_CHOICE.Cancel;
    }

    internal static CRecipeConfirmDialog CreateDefaultRestartChoice(string message)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new CRecipeConfirmDialog("Default 전체 실행 재시작", message, "검사부터 다시", useDangerStyle: false);
        if (owner is not null) dialog.Owner = owner;
        dialog.CancelButton.Content = "처음부터";
        dialog.AbortGap.Width = new GridLength(12);
        dialog.AbortColumn.Width = new GridLength(1, GridUnitType.Star);
        dialog.AbortButton.Visibility = Visibility.Visible;
        return dialog;
    }

    internal static CRecipeConfirmDialog CreateFineRetryChoice(string message)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new CRecipeConfirmDialog("FINE NG 재검사", message, "NG 라인부터", useDangerStyle: false);
        if (owner is not null)
            dialog.Owner = owner;
        dialog.CancelButton.Content = "처음부터";
        return dialog;
    }

    private static void ShowMessage(string title, string message, bool useDangerStyle, Window? owner)
    {
        var dialog = new CRecipeConfirmDialog(title, message, "확인", useDangerStyle)
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(dialog.ConfirmButton, 3);
        dialog.ShowDialog();
    }

    public CRecipeConfirmDialog(string title, string message, string confirmText = "확인", bool useDangerStyle = true)
    {
        InitializeComponent();
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40);
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        if (!useDangerStyle)
        {
            ConfirmButton.SetResourceReference(Control.BackgroundProperty, "ButtonSuccessBrush");
            ConfirmButton.SetResourceReference(Control.BorderBrushProperty, "ButtonSuccessBorderBrush");
        }

        ConfirmButton.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            _alternateSelected = false;
            DialogResult = false;
        }
    }

    private void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _alternateSelected = true;
        DialogResult = false;
    }

    private void OnAbortClicked(object sender, RoutedEventArgs e)
    {
        _alternateSelected = false;
        DialogResult = false;
    }
}

public enum EN_REVIEW_RESUME_CHOICE
{
    Cancel,
    Resume,
    Restart
}
