using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace AiyoPerps.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly ResourceManager ResourceManager = new("AiyoPerps.Resources.UiText", typeof(LocalizationService).Assembly);
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key]
    {
        get
        {
            var text = ResourceManager.GetString(key, _culture);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var fallback = ResourceManager.GetString(key, CultureInfo.GetCultureInfo("zh-TW"));
            return string.IsNullOrWhiteSpace(fallback) ? key : fallback;
        }
    }

    public string CurrentLanguageCode =>
        string.Equals(_culture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh-TW";

    public void SetLanguage(string? languageCode)
    {
        var next = string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("en")
            : CultureInfo.GetCultureInfo("zh-TW");

        if (string.Equals(_culture.Name, next.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _culture = next;
        CultureInfo.CurrentUICulture = next;
        CultureInfo.CurrentCulture = next;
        RaisePropertyChanged(nameof(CurrentLanguageCode));
        RaisePropertyChanged("Item");
        RaisePropertyChanged("Item[]");
        RaisePropertyChanged(string.Empty);
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
