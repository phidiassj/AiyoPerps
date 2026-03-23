using AiyoPerps.Services;
using System.Globalization;

namespace AiyoPerps.ViewModels;

public sealed class AIAgentRunDetailViewModel : ViewModelBase
{
    public AIAgentRunDetailViewModel(AIAgentRunRecord record)
    {
        Record = record;
    }

    public AIAgentRunRecord Record { get; }

    public string AgentDisplayName => AIAgentProfileCatalog.ToDisplayName(Record.AgentType);

    public string StartedAtDisplay => Record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public string FinishedAtDisplay => Record.FinishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "-";

    public string DurationDisplay
    {
        get
        {
            if (!Record.FinishedAt.HasValue)
            {
                return "-";
            }

            var duration = Record.FinishedAt.Value - Record.StartedAt;
            return duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        }
    }
}
