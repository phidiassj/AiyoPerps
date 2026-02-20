using System;

namespace AiyoPerps.Services;

public sealed class ToastMessage
{
    public required string Message { get; init; }
    public required string BackgroundHex { get; init; }
    public required string BorderHex { get; init; }
}

public sealed class ToastService
{
    public event Action<ToastMessage>? ToastRaised;

    public void ShowInfo(string message)
    {
        ToastRaised?.Invoke(new ToastMessage
        {
            Message = message,
            BackgroundHex = "#163744",
            BorderHex = "#2C667A"
        });
    }

    public void ShowWarning(string message)
    {
        ToastRaised?.Invoke(new ToastMessage
        {
            Message = message,
            BackgroundHex = "#4A2F12",
            BorderHex = "#8A5A20"
        });
    }

    public void ShowError(string message)
    {
        ToastRaised?.Invoke(new ToastMessage
        {
            Message = message,
            BackgroundHex = "#4A1E25",
            BorderHex = "#8D3343"
        });
    }
}
