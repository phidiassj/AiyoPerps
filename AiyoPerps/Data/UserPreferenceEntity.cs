using System;
using System.ComponentModel.DataAnnotations;

namespace AiyoPerps.Data;

public sealed class UserPreferenceEntity
{
    [Key]
    [MaxLength(64)]
    public required string PreferenceKey { get; set; }

    [MaxLength(256)]
    public required string PreferenceValue { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
