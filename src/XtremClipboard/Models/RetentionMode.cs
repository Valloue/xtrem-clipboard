namespace XtremClipboard.Models;

/// <summary>
/// Mode de limite d'historique (D-STORE-LIMITS).
/// </summary>
public enum RetentionMode
{
    ByTime = 0,
    ByDiskSize = 1,
    ByEntryCount = 2
}
