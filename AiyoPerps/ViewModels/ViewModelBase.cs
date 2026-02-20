using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AiyoPerps.Services;

namespace AiyoPerps.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public LocalizationService L => App.Localization;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void NotifyLocalizationChanged()
    {
        RaisePropertyChanged(nameof(L));
    }
}
