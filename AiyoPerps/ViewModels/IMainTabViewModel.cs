namespace AiyoPerps.ViewModels;

public interface IMainTabViewModel
{
    string Header { get; }

    bool IsClosable { get; }

    void NotifyLocalizationChanged();
}
