namespace ThriveDevCenter.Server.Models;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shared.Models;
using Shared.Notifications;
using SmartFormat;
using Utilities;

/// <summary>
///   Feed combined from multiple
/// </summary>
[Index(nameof(Name), IsUnique = true)]
public class CombinedFeed : FeedBase, IUpdateNotifications
{
    public CombinedFeed(string name, string htmlFeedItemEntryTemplate) : base(name)
    {
        HtmlFeedItemEntryTemplate = htmlFeedItemEntryTemplate;
    }

    [Required]
    [UpdateFromClientRequest]
    public string HtmlFeedItemEntryTemplate { get; set; }

    [Required]
    [UpdateFromClientRequest]
    public TimeSpan CacheTime { get; set; }

    public ICollection<Feed> CombinedFromFeeds { get; set; } = new HashSet<Feed>();

    public void ProcessContent(IEnumerable<Feed> dataSources)
    {
        var allItems = dataSources.SelectMany(s =>
                s.ParseContent(s.LatestContent ?? throw new ArgumentException("feed doesn't have latest content"),
                    out _))
            .OrderByDescending(i => i.PublishedAt).Take(MaxItems);

        var builder = new StringBuilder();

        foreach (var item in allItems)
        {
            builder.Append(Smart.Format(HtmlFeedItemEntryTemplate, item.GetFormatterData(Name)));
        }

        var newContent = builder.ToString();

        if (newContent != LatestContent)
        {
            ContentUpdatedAt = DateTime.UtcNow;
            LatestContent = newContent;
        }
    }

    public CombinedFeedDTO GetDTO()
    {
        return new()
        {
            Id = Id,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = Name,
            MaxItems = MaxItems,
            CombinedFromFeeds = CombinedFromFeeds.Select(f => f.Id).ToList(),
            DeletedCombinedFromFeeds = CombinedFromFeeds.Where(f => f.Deleted).Select(f => f.Name).ToList(),
            HtmlFeedItemEntryTemplate = HtmlFeedItemEntryTemplate,
            CacheTime = CacheTime,
        };
    }

    public IEnumerable<Tuple<SerializedNotification, string>> GetNotifications(EntityState entityState)
    {
        yield return new Tuple<SerializedNotification, string>(
            new CombinedFeedListUpdated() { Item = GetDTO() },
            NotificationGroups.CombinedFeedListUpdated);
    }
}
