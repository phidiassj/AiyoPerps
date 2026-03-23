namespace AiyoPerps.ViewModels;

public sealed class ConfirmationDialogViewModel : ViewModelBase
{
    public ConfirmationDialogViewModel(string title, string message, string confirmText, string cancelText)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
    }

    public string Title { get; }

    public string Message { get; }

    public string ConfirmText { get; }

    public string CancelText { get; }
}
