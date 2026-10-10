namespace GmailWatcher.Google.Gmail;

/// <summary>
/// Quota units Gmail charges per call (https://developers.google.com/gmail/api/reference/quota). Google limits a
/// user to 6,000 units per minute, so a message download (20) is what bounds the sync speed.
/// </summary>
public static class GmailQuotaCosts
{
    public const int GetProfile = 1;
    public const int ListLabels = 1;
    public const int ListMessages = 5;
    public const int ListHistory = 2;
    public const int GetMessage = 20;
    public const int GetSetting = 1;

    /// <summary>The most expensive single call; the governor's burst capacity is never smaller than this.</summary>
    public const int LargestSingleCost = GetMessage;
}
