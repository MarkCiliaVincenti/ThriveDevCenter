namespace ThriveDevCenter.Server.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Models;
using Shared.Models;
using Shared.Utilities;
using SharedBase.Utilities;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;
using Svg.Skia;
using Utilities;
using Color = SixLabors.ImageSharp.Color;
using Image = SixLabors.ImageSharp.Image;

/// <summary>
///   Handles running the Revolutionary Bot for Discord. Used on the Thrive community discord
/// </summary>
public sealed class RevolutionaryDiscordBotService : IDisposable
{
    private const int SecondsBetweenSameCommand = 10;

    private static readonly TimeSpan
        CommandIntervalBeforeRunningAgain = TimeSpan.FromSeconds(SecondsBetweenSameCommand);

    private static readonly TimeSpan
        DisconnectedTimeBeforeAssumePermanentFailure = TimeSpan.FromMinutes(8);

    private readonly ILogger<RevolutionaryDiscordBotService> logger;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ApplicationDbContext database;

    /// <summary>
    ///   Used to limit how many tasks are at once running expensive operations
    /// </summary>
    private readonly SemaphoreSlim expensiveOperationLimiter = new(1);

    private readonly SemaphoreSlim databaseReadWriteLock = new(1);

    private readonly Dictionary<string, DateTime> lastRanCommands = new();

    private readonly string botToken;
    private readonly ulong? primaryGuild;
    private readonly Uri wikiUrlBase;
    private readonly Uri overallTranslationStatusUrl;
    private readonly Uri translationProgressUrl;
    private readonly Uri wikiDefaultPreviewImage;
    private readonly Uri progressFontUrl;
    private readonly Uri progressImageUrl;
    private readonly Uri releaseStatsApiUrl;
    private readonly Regex underWaterCivRegex;
    private readonly Regex sentientPlantRegex;

    private DiscordSocketClient? client;

    private TimedResourceCache<List<GithubMilestone>>? githubMilestones;
    private Lazy<Task<FontCollection>>? fonts;
    private DisposableTimedResourceCache<Image>? progressBackgroundImage;

    private DisposableTimedResourceCache<Image>? overallTranslationStatus;
    private DisposableTimedResourceCache<Image>? translationProgress;

    private DateTime? disconnectedSince;

    public RevolutionaryDiscordBotService(ILogger<RevolutionaryDiscordBotService> logger, IConfiguration configuration,
        IHttpClientFactory httpClientFactory, ApplicationDbContext database)
    {
        this.logger = logger;
        this.httpClientFactory = httpClientFactory;
        this.database = database;

        botToken = configuration["Discord:RevolutionaryBot:Token"] ?? string.Empty;
        var guild = configuration["Discord:RevolutionaryBot:PrimaryGuild"];

        wikiUrlBase = new Uri(configuration["Discord:RevolutionaryBot:WikiBaseUrl"] ??
            throw new InvalidOperationException("Bot default variables need to exist when when not used"));
        translationProgressUrl = new Uri(configuration["Discord:RevolutionaryBot:TranslationProgressUrl"] ??
            throw new InvalidOperationException("Bot default variables need to exist when when not used"));
        overallTranslationStatusUrl = new Uri(configuration["Discord:RevolutionaryBot:OverallTranslationStatusUrl"] ??
            throw new InvalidOperationException("Bot default variables need to exist when when not used"));
        wikiDefaultPreviewImage = new Uri(configuration["Discord:RevolutionaryBot:WikiDefaultPreviewImage"] ??
            throw new InvalidOperationException("Bot default variables need to exist when when not used"));

        progressFontUrl = configuration.BaseUrlRelative("Discord:RevolutionaryBot:ProgressFont");
        progressImageUrl = configuration.BaseUrlRelative("Discord:RevolutionaryBot:ProgressBackgroundImage");

        releaseStatsApiUrl = new Uri(configuration.GetBaseUrl(), "/api/v1/ReleaseStats");

        // Probably a bit obsessive for this one joke, as it's still pretty easy to bypass
        underWaterCivRegex = new Regex(
            @"un.*(\*|w)(\s|_|\.|\*)*(\*|a)(\s|_|\.|\*)*(\*|t)(\s|_|\.|\*)*(\*|e)(\s|_|\.|\*)*r(\s|_|\.|\*)*c(\*|i)v",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        sentientPlantRegex = new Regex(
            @"(sentient(\s|_|\.|\*)*plant|plant(\s|_|\.|\*)*c(\*|i)v)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        if (string.IsNullOrEmpty(botToken))
            return;

        if (guild != null)
            primaryGuild = ulong.Parse(guild);

        Configured = true;
    }

    public bool Configured { get; }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Configured)
        {
            logger.LogInformation("Revolutionary Bot is not configured for use");
            return;
        }

        SetupCachedResourceFetching();

        // This loop is here to allow us to recreate the client if it entirely fails to connect
        while (!stoppingToken.IsCancellationRequested)
        {
            disconnectedSince = DateTime.UtcNow;

            logger.LogInformation("Revolutionary Bot for Discord is starting");

            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
            };

            client = new DiscordSocketClient(config);
            client.Log += Log;
            client.Ready += ClientReady;
            client.SlashCommandExecuted += SlashCommandHandler;
            client.MessageReceived += MessageHandler;
            client.Connected += OnConnected;
            client.Disconnected += OnDisconnected;

            await client.LoginAsync(TokenType.Bot, botToken);
            await client.StartAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait until we are stopped (or we need to restart the client)
                await Task.Delay(DisconnectedTimeBeforeAssumePermanentFailure, stoppingToken);

                if (disconnectedSince != null && !stoppingToken.IsCancellationRequested)
                {
                    TimeSpan failureTime;
                    try
                    {
                        failureTime = DateTime.UtcNow - disconnectedSince.Value;
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning(e, "Failed to get time since bot disconnect");
                        continue;
                    }

                    if (failureTime > DisconnectedTimeBeforeAssumePermanentFailure)
                    {
                        logger.LogError(
                            "Discord bot has been in disconnected state for {FailureTime}, force recreating client",
                            failureTime);

                        var oldClient = client;

                        // Run this cleanup in the background in case this is permanently stuck
                        async void CleanupOldClient()
                        {
                            logger.LogInformation("Trying to stop old failed discord client");
                            try
                            {
                                await oldClient.StopAsync();
                                await oldClient.DisposeAsync();
                                logger.LogInformation("Old discord client stopped");
                            }
                            catch (Exception e)
                            {
                                logger.LogWarning(e, "Old discord client stop failure");
                            }
                        }

                        var stopTask = new Task(CleanupOldClient);

                        stopTask.Start();

                        client = null;
                        break;
                    }
                }
            }

            if (client != null)
                await client.StopAsync();
        }
    }

    public void Dispose()
    {
        expensiveOperationLimiter.Dispose();
        databaseReadWriteLock.Dispose();
        client?.Dispose();
        githubMilestones?.Dispose();
        progressBackgroundImage?.Dispose();
        overallTranslationStatus?.Dispose();
        translationProgress?.Dispose();
    }

    private Task OnConnected()
    {
        logger.LogTrace("We are now connected to Discord");
        disconnectedSince = null;
        return Task.CompletedTask;
    }

    private Task OnDisconnected(Exception arg)
    {
        if (disconnectedSince != null)
            return Task.CompletedTask;

        logger.LogInformation(
            "We got disconnected from Discord, we'll wait {WaitTime} before force recreating the client",
            DisconnectedTimeBeforeAssumePermanentFailure);

        disconnectedSince = DateTime.UtcNow;

        return Task.CompletedTask;
    }

    private void SetupCachedResourceFetching()
    {
        // Load fonts once
        fonts = new Lazy<Task<FontCollection>>(() => DownloadFont(progressFontUrl));

        // Setup other cached resource loading
        progressBackgroundImage =
            new DisposableTimedResourceCache<Image>(() => DownloadImage(progressImageUrl), TimeSpan.FromMinutes(15));

        overallTranslationStatus =
            new DisposableTimedResourceCache<Image>(() => DownloadImage(overallTranslationStatusUrl),
                TimeSpan.FromMinutes(5));
        translationProgress = new DisposableTimedResourceCache<Image>(
            () => DownloadSvg(translationProgressUrl), TimeSpan.FromMinutes(5));

        githubMilestones = new TimedResourceCache<List<GithubMilestone>>(async () =>
        {
            // TODO: if we have more than 100 milestones only the latest 100 can be used like this
            var httpClient = httpClientFactory.CreateClient("github");

            return await httpClient.GetFromJsonAsync<List<GithubMilestone>>(QueryHelpers.AddQueryString(
                "repos/Revolutionary-Games/Thrive/milestones", new Dictionary<string, string?>
                {
                    { "state", "all" },
                    { "per_page", "100" },
                    { "direction", "desc" },
                })) ?? throw new NullDecodedJsonException();
        }, TimeSpan.FromMinutes(1));
    }

    private async Task ClientReady()
    {
        if (primaryGuild != null)
        {
            try
            {
                var guild = client!.GetGuild(primaryGuild.Value);

                await guild.CreateApplicationCommandAsync(BuildProgressCommand().Build());
                await guild.CreateApplicationCommandAsync(BuildLanguageCommand().Build());
                await guild.CreateApplicationCommandAsync(BuildWikiCommand().Build());
                await guild.CreateApplicationCommandAsync(BuildReleasesCommand().Build());
                await guild.CreateApplicationCommandAsync(BuildDaysSinceCommand().Build());
            }
            catch (HttpException e)
            {
                logger.LogError(e, "Failed to register one or more guild commands");
            }
        }

        await databaseReadWriteLock.WaitAsync();
        try
        {
            // If commands are changed the version numbers here *must* be updated
            bool changes = await RegisterGlobalCommandIfRequired(BuildProgressCommand(), 2);

            if (await RegisterGlobalCommandIfRequired(BuildLanguageCommand(), 2))
                changes = true;
            if (await RegisterGlobalCommandIfRequired(BuildWikiCommand(), 2))
                changes = true;
            if (await RegisterGlobalCommandIfRequired(BuildReleasesCommand(), 2))
                changes = true;
            if (await RegisterGlobalCommandIfRequired(BuildDaysSinceCommand(), 2))
                changes = true;

            if (await RegisterKeywordIfRequired("underWaterCiv", "Underwater Civs"))
                changes = true;
            if (await RegisterKeywordIfRequired("sentientPlant", "Sentient Plants"))
                changes = true;

            if (changes)
            {
                logger.LogInformation("Global commands have been updated, saving info to database");
                await database.SaveChangesAsync();
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Registering global commands failed");

            // If the bot can't register commands, it might as well not run
            throw;
        }
        finally
        {
            databaseReadWriteLock.Release();
        }
    }

    private async Task<bool> RegisterGlobalCommandIfRequired(SlashCommandBuilder builder, int version)
    {
        if (client == null)
            throw new InvalidOperationException("Discord client not initialized");

        // Only call global command registration when necessary
        var key = GlobalDiscordBotCommand.GenerateKey(builder.Name, version);

        if (await database.GlobalDiscordBotCommands.FindAsync(key) != null)
            return false;

        await database.GlobalDiscordBotCommands.AddAsync(new GlobalDiscordBotCommand(key));

        try
        {
            await client.CreateGlobalApplicationCommandAsync(builder.Build());
        }
        catch (HttpException e)
        {
            logger.LogError(e, "Failed to register global command {Key}", key);
            return false;
        }

        logger.LogInformation("Registered global Discord command {Key}", key);
        return true;
    }

    private async Task<bool> RegisterKeywordIfRequired(string key, string title)
    {
        if (await database.WatchedKeywords.FindAsync(key) != null)
            return false;

        await database.WatchedKeywords.AddAsync(new WatchedKeyword(key, title));
        return true;
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        switch (command.CommandName)
        {
            case "progress":
                await HandleProgressCommand(command);
                break;
            case "language":
                await HandleLanguageCommand(command);
                break;
            case "wiki":
                await HandleWikiCommand(command);
                break;
            case "releases":
                await HandleReleasesCommand(command);
                break;
            case "dayssince":
                await HandleDaysSinceCommand(command);
                break;
            default:
                await command.RespondAsync("Unknown command! Type `/` to see available commands");
                break;
        }
    }

    private async Task HandleProgressCommand(SocketSlashCommand command)
    {
        var versionOption = command.Data.Options.FirstOrDefault();

        string? version = null;

        if (versionOption is { Type: ApplicationCommandOptionType.String })
        {
            version = (string)versionOption.Value;

            if (string.IsNullOrWhiteSpace(version))
            {
                version = null;
            }
        }

        if (!await CheckCanRunAgain(command, $"progress-{version}"))
            return;

        await command.DeferAsync();

#pragma warning disable CS4014 // we don't want to hold up the command processing
        PerformSlowProgressPart(command, version);
#pragma warning restore CS4014
    }

    private async Task PerformSlowProgressPart(SocketSlashCommand command, string? version)
    {
        List<GithubMilestone> milestones;
        try
        {
            milestones = await githubMilestones!.GetData();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Github connection error");
            await command.ModifyOriginalResponseAsync(properties => properties.Content = "Query to Github failed");
            return;
        }

        GithubMilestone? milestone;

        if (version == null)
        {
            milestone = milestones.Where(m => m.State == "open" && m.DueOn != null).MinBy(m => m.DueOn);

            milestone ??= milestones.Where(m => m.State == "open").MinBy(m => m.CreatedAt);
        }
        else
        {
            // First exact or ends with match
            milestone = milestones.FirstOrDefault(m => m.Title.Equals(version)) ??
                milestones.FirstOrDefault(m => m.Title.EndsWith(version));

            // Then just a simple contains in sorted order
            milestone ??= milestones.OrderByDescending(m => m.DueOn).FirstOrDefault(m => m.Title.Contains(version));
        }

        if (milestone == null)
        {
            await command.ModifyOriginalResponseAsync(properties => properties.Content = "No matching milestone found");
            return;
        }

        var totalIssues = milestone.OpenIssues + milestone.ClosedIssues;

        var completionFraction = totalIssues > 0 ? (float)milestone.ClosedIssues / totalIssues : 0.0f;
        var percentage = (float)Math.Round(completionFraction * 100);

        var openText = "open issue".PrintCount(milestone.OpenIssues);
        var closedText = "closed issue".PrintCount(milestone.ClosedIssues);

        var embedBuilder = new EmbedBuilder().WithTitle(milestone.Title)
            .WithDescription($"{percentage}% done with {openText} {closedText}")
            .WithTimestamp(milestone.UpdatedAt).WithUrl(milestone.HtmlUrl);

        if (milestone.DueOn != null)
            embedBuilder.WithFooter($"Due by {milestone.DueOn.Value:yyyy-MM-dd}");

        Image backgroundImage;
        FontCollection fontCollection;

        try
        {
            var fontWaitTask = fonts!.Value;
            backgroundImage = await progressBackgroundImage!.GetData();
            fontCollection = await fontWaitTask;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error getting progress image resources");
            await command.ModifyOriginalResponseAsync(properties =>
            {
                properties.Content = "Failed to get progress image drawing resources";
                properties.Embeds = new[] { embedBuilder.Build() };
            });
            return;
        }

        using var tempFileData = new MemoryStream();

        await expensiveOperationLimiter.WaitAsync();
        try
        {
            // We assume the size of the background image here
            int width = 700;
            int height = 300;

            var titleFont = fontCollection.Families.First().CreateFont(56, FontStyle.Bold);
            var percentageFont = fontCollection.Families.First().CreateFont(36, FontStyle.Bold);
            var issueCountFont = fontCollection.Families.First().CreateFont(36, FontStyle.Bold);

            using var progressImage = new Image<Rgb24>(width, height);

            // ReSharper disable AccessToDisposedClosure
            // Draw the background image
            progressImage.Mutate(x => { x.DrawImage(backgroundImage, 1); });

            // ReSharper restore AccessToDisposedClosure

            progressImage.Mutate(x =>
            {
                var textBrush = Brushes.Solid(Color.White);
                var titlePen = Pens.Solid(Color.Black, 3.0f);
                var textPen = Pens.Solid(Color.Black, 2.0f);

                x.DrawText(milestone.Title, titleFont, textBrush, titlePen, new PointF(15, 5));

                // Issues
                var issuesY = 90;
                x.DrawText(openText, issueCountFont, textBrush, textPen, new PointF(30, issuesY));
                x.DrawText(closedText, issueCountFont, textBrush, textPen, new PointF(350, issuesY));

                // Progress bar
                // TODO: make rounded corners for the bar
                var barPen = Pens.Solid(Color.Black, 7.0f);
                var barBrush = Brushes.Solid(new Color(new Rgb24(63, 169, 82)));

                // TODO: gradient to:
                // new SixLabors.ImageSharp.Color(new Rgb24(134, 207, 147)));

                x.Fill(barBrush, new RectangularPolygon(30, 150, (width - 60) * completionFraction, 50));
                x.Draw(barPen, new RectangularPolygon(30, 150, width - 60, 50));

                // Percentage
                x.DrawText($"{percentage}%", percentageFont, textBrush, textPen, new PointF(50, 210));
            });

            await progressImage.SaveAsync(tempFileData, PngFormat.Instance);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Progress image drawing problem");
            await command.ModifyOriginalResponseAsync(properties =>
            {
                properties.Content = "Failed to draw progress image";
                properties.Embeds = new[] { embedBuilder.Build() };
            });
            return;
        }
        finally
        {
            expensiveOperationLimiter.Release();
        }

        tempFileData.Position = 0;
        await command.FollowupWithFileAsync(tempFileData, "progress.png", string.Empty, new[] { embedBuilder.Build() });
    }

    private SlashCommandBuilder BuildProgressCommand()
    {
        return new SlashCommandBuilder()
            .WithName("progress")
            .WithDescription(
                "Displays the progress to the next release or, if a version number is specified, for that version.")
            .AddOption("version", ApplicationCommandOptionType.String, "The version to display progress for", false)
            .WithDMPermission(false);
    }

    private async Task HandleLanguageCommand(SocketSlashCommand command)
    {
        if (!await CheckCanRunAgain(command))
            return;

        await command.DeferAsync();

#pragma warning disable CS4014 // we don't want to hold up the command processing
        PerformSlowLanguagePart(command);
#pragma warning restore CS4014
    }

    private async Task PerformSlowLanguagePart(SocketSlashCommand command)
    {
        Image overallStatusImage;
        Image progressImage;

        try
        {
            var overallWaitTask = overallTranslationStatus!.GetData();
            progressImage = await translationProgress!.GetData();
            overallStatusImage = await overallWaitTask;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Weblate connection error");
            await command.ModifyOriginalResponseAsync(properties =>
                properties.Content = "Failed to get data from Weblate");
            return;
        }

        using var tempFileData = new MemoryStream();

        await expensiveOperationLimiter.WaitAsync();
        try
        {
            // We assume the overall status image is narrower
            using var languagesImage = new Image<Rgb24>(progressImage.Width + 10,
                overallStatusImage.Height + progressImage.Height + 15);

            // Clear everything to white first before drawing
            languagesImage.ProcessPixelRows(accessor =>
            {
                var white = new Rgb24(255, 255, 255);

                for (int y = 0; y < accessor.Height; y++)
                {
                    foreach (ref Rgb24 pixel in accessor.GetRowSpan(y))
                    {
                        pixel = white;
                    }
                }
            });

            // ReSharper disable AccessToDisposedClosure
            languagesImage.Mutate(x =>
            {
                x.DrawImage(progressImage, new Point(5, overallStatusImage.Height + 10), 1);
            });

            languagesImage.Mutate(x =>
                x.DrawImage(overallStatusImage, new Point(languagesImage.Width / 2 - overallStatusImage.Width / 2, 5),
                    1));

            // ReSharper restore AccessToDisposedClosure
            await languagesImage.SaveAsync(tempFileData, PngFormat.Instance);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Language image drawing problem");
            await command.ModifyOriginalResponseAsync(
                properties => properties.Content = "Failed to draw language progress image");
            return;
        }
        finally
        {
            expensiveOperationLimiter.Release();
        }

        tempFileData.Position = 0;
        await command.FollowupWithFileAsync(tempFileData, "language.png");
    }

    private SlashCommandBuilder BuildLanguageCommand()
    {
        return new SlashCommandBuilder()
            .WithName("language")
            .WithDescription(
                "Displays current language translation statistics for Thrive.");
    }

    private async Task HandleDaysSinceCommand(SocketSlashCommand command)
    {
        if (!await CheckCanRunAgain(command))
            return;

        WatchedKeyword? keyword;

        try
        {
            keyword = await database.WatchedKeywords.FindAsync(
                command.Data.Options.First().Value);

            if (keyword == null)
            {
                logger.LogWarning($"Watched Keyword {command.Data.Options.First().Value} is null");
                await command.RespondAsync("Failed to retrive data");
                return;
            }
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Empty Command Options Passed to dayssince");
            await command.RespondAsync("Empty Command Options Passed to dayssince");
            return;
        }

        await command.DeferAsync();

#pragma warning disable CS4014 // we don't want to hold up the command processing

        // ReSharper disable once StringLiteralTypo
        SlowDaysSince(command, keyword);
#pragma warning restore CS4014
    }

    private async Task SlowDaysSince(SocketSlashCommand command, WatchedKeyword keyword)
    {
        await expensiveOperationLimiter.WaitAsync();
        try
        {
            using var tempFileData = new MemoryStream();
            var daysSinceImage = await GenerateDaysSinceImage(keyword);

            await daysSinceImage.SaveAsync(tempFileData, PngFormat.Instance);

            tempFileData.Position = 0;
            await command.FollowupWithFileAsync(tempFileData, "last_said.png", string.Empty);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Days since image drawing problem");
        }
        finally
        {
            expensiveOperationLimiter.Release();
        }
    }

    private async Task SlowDaysSince(SocketMessage message, WatchedKeyword keyword)
    {
        await expensiveOperationLimiter.WaitAsync();
        try
        {
            using var tempFileData = new MemoryStream();
            var daysSinceImage = await GenerateDaysSinceImage(keyword);

            await daysSinceImage.SaveAsync(tempFileData, PngFormat.Instance);

            tempFileData.Position = 0;
            await message.Channel.SendFileAsync(tempFileData, "last_said.png", string.Empty);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Days since image drawing problem");
        }
        finally
        {
            expensiveOperationLimiter.Release();
        }
    }

    private async Task<Image<Rgb24>> GenerateDaysSinceImage(WatchedKeyword keyword)
    {
        FontCollection fontCollection;

        var fontWaitTask = fonts!.Value;
        fontCollection = await fontWaitTask;

        int width = 600;
        int height = 750;

        var titleLineFont = fontCollection.Families.First().CreateFont(45, FontStyle.Bold);
        var titleFont = fontCollection.Families.First().CreateFont(60, FontStyle.Bold);
        var dayFont = fontCollection.Families.First().CreateFont(300, FontStyle.Bold);
        var subtextFont = fontCollection.Families.First().CreateFont(20, FontStyle.Regular);

        TextOptions titleLineOptions = new(titleLineFont);
        TextOptions titleOptions = new(titleFont);
        TextOptions dayOptions = new(dayFont);
        TextOptions subtextOptions = new(subtextFont);

        string tagLine = "Days Since Last Mention Of";
        string subtext = "Unofficial Thrive Release Date: " +
            (DateTime.Today.Year + 10 + await database.WatchedKeywords.SumAsync(val => val.TotalCount)).ToString();
        int dayCount = (DateTime.UtcNow.Date - keyword.LastSeen.Date).Days;

        if (dayCount < 1)
            dayCount = 0;

        var daysSinceImage = new Image<Rgb24>(width, height, Color.White);

        daysSinceImage.Mutate(x =>
        {
            var textBrush = Brushes.Solid(Color.Black);
            var titlePen = Pens.Solid(Color.Black, 3.0f);
            var titleBrush = Brushes.Solid(Color.FromRgb(226, 7, 33));
            var textPen = Pens.Solid(Color.Black, 2.0f);
            var subtextPen = Pens.Solid(Color.Black, 1.0f);

            var titleLineOffset = TextMeasurer.Measure(tagLine, titleLineOptions).Width / 2.0f;
            var titleOffset = TextMeasurer.Measure(keyword.Title, titleOptions).Width / 2.0f;
            var dayOffset = TextMeasurer.Measure(dayCount.ToString(), dayOptions).Width / 2.0f;
            var subtextOffset = TextMeasurer.Measure(subtext, subtextOptions).Width / 2.0f;

            x.Draw(textPen, new RectangularPolygon(0, 0, width, height));
            x.Fill(titleBrush, new RectangularPolygon(0, 0, width, 125));

            x.DrawText(tagLine, titleLineFont, textBrush, titlePen, new PointF((width / 2.0f) - titleLineOffset, 160));
            x.DrawText(keyword.Title, titleFont, textBrush, titlePen, new PointF((width / 2.0f) - titleOffset, 220));

            x.DrawText(dayCount.ToString(), dayFont, textBrush, titlePen, new PointF((width / 2.0f) - dayOffset, 300));

            x.DrawText(subtext, subtextFont, textBrush, subtextPen, new PointF((width / 2.0f) - subtextOffset,
                height - 25));
        });

        return daysSinceImage;
    }

    private SlashCommandBuilder BuildDaysSinceCommand()
    {
        return new SlashCommandBuilder()
            .WithName("dayssince")
            .WithDescription(
                "Displays how many days have passed since a keyword was last brought up")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("keyword")
                .WithRequired(true)
                .WithDescription("Sets what keyword to display")
                .AddChoice("UnderwaterCivs", "underWaterCiv")
                .AddChoice("SentientPlants", "sentientPlant")
                .WithType(ApplicationCommandOptionType.String));
    }

    private async Task HandleWikiCommand(SocketSlashCommand command)
    {
        var pageOption = command.Data.Options.FirstOrDefault();

        string? page = null;

        if (pageOption is { Type: ApplicationCommandOptionType.String })
        {
            page = (string)pageOption.Value;

            // Convert spaces to underscores to make the links work
            // TODO: somehow also fix capitalization or implement using the wiki search to find almost matching pages
            page = page.Replace(' ', '_');
        }

        if (string.IsNullOrWhiteSpace(page))
        {
            await command.RespondAsync("Please provide a wiki page title");
            return;
        }

        if (page.Contains("../"))
        {
            await command.RespondAsync("Invalid page title");
            return;
        }

        if (!await CheckCanRunAgain(command, page))
            return;

        await command.DeferAsync();

        var httpClient = httpClientFactory.CreateClient();

        var finalUrl = new Uri(wikiUrlBase, page);

        HttpResponseMessage response;

        try
        {
            response = await httpClient.GetAsync(finalUrl);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to connect to the wiki");
            await command.ModifyOriginalResponseAsync(properties =>
                properties.Content = "Failed to connect to the wiki");
            return;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            await command.ModifyOriginalResponseAsync(properties =>
                properties.Content =
                    "The requested wiki page does not exist. Note that multi word titles are case sensitive");
            return;
        }

        DateTime? updatedTime = null;

        if (response.Content.Headers.TryGetValues("Last-Modified", out var lastModifiedHeader))
        {
            var rawDate = lastModifiedHeader.FirstOrDefault();

            if (!string.IsNullOrEmpty(rawDate))
            {
                if (!DateTime.TryParse(rawDate, out var parsed))
                {
                    logger.LogError("Failed to parse wiki modified date from: {RawDate}", rawDate);
                }
                else
                {
                    updatedTime = parsed;
                }
            }
        }

        var parser = new HtmlParser();
        var document = parser.ParseDocument(await response.Content.ReadAsStringAsync());

        var title = document.Title ?? "Unknown title";

        var description = "Page description not found";

        var descriptionElement = document.QuerySelectorAll("meta[name=\"description\"]")
            .FirstOrDefault();

        if (descriptionElement != null)
            description = descriptionElement.GetAttribute("content") ?? "Content attribute not found";

        // Primary content could be parsed inside div id="mw-content-text"

        var embedBuilder = new EmbedBuilder().WithTitle(title).WithDescription(description)
            .WithImageUrl(wikiDefaultPreviewImage.ToString()).WithUrl(finalUrl.ToString())
            .WithColor(new Discord.Color(182, 9, 9));

        if (updatedTime != null)
            embedBuilder.WithTimestamp(updatedTime.Value);

        await command.ModifyOriginalResponseAsync(properties =>
        {
            properties.Embeds = new[] { embedBuilder.Build() };
        });
    }

    private SlashCommandBuilder BuildWikiCommand()
    {
        return new SlashCommandBuilder()
            .WithName("wiki")
            .WithDescription(
                "Get a summary of a Thrive wiki page.")
            .AddOption("page-title", ApplicationCommandOptionType.String, "The version to display progress for", true);
    }

    private async Task HandleReleasesCommand(SocketSlashCommand command)
    {
        if (!await CheckCanRunAgain(command))
            return;

        await command.DeferAsync();

#pragma warning disable CS4014 // we don't want to hold up the command processing
        PerformSlowReleasesPart(command);
#pragma warning restore CS4014
    }

    private async Task PerformSlowReleasesPart(SocketSlashCommand command)
    {
        var httpClient = httpClientFactory.CreateClient();

        List<RepoReleaseStats> stats;

        try
        {
            stats = await httpClient.GetFromJsonAsync<List<RepoReleaseStats>>(releaseStatsApiUrl) ??
                throw new NullDecodedJsonException();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to connect to the release stats API");
            await command.ModifyOriginalResponseAsync(properties =>
                properties.Content = "Failed to read release stats API data");
            return;
        }

        var embeds = new List<EmbedBuilder>();

        foreach (var repoStats in stats)
        {
            // Max of 10 for now. The Discord API probably allows 20 but I couldn't find a constant for that
            if (embeds.Count >= 10)
                break;

            var embedBuilder = new EmbedBuilder().WithTitle(repoStats.Repository)
                .WithUrl($"https://github.com/{repoStats.Repository}")
                .WithFooter($"Total releases: {repoStats.TotalReleases}");

            if (repoStats.LatestRelease != null && repoStats.LatestReleaseTime != null)
            {
                embedBuilder.AddField(fieldBuilder =>
                {
                    fieldBuilder.Name = "Latest release";
                    fieldBuilder.Value = $"{repoStats.LatestRelease} on {repoStats.LatestReleaseTime.Value:yyyy-MM-dd}";
                });
                embedBuilder.AddField(fieldBuilder =>
                {
                    fieldBuilder.Name = "Downloads";
                    fieldBuilder.Value = repoStats.LatestDownloads;
                });
                embedBuilder.AddField(fieldBuilder =>
                {
                    fieldBuilder.Name = "Downloads per day";
                    fieldBuilder.Value = repoStats.LatestDownloadsPerDay;
                });
                embedBuilder.AddField(fieldBuilder =>
                {
                    fieldBuilder.Name = "Linux/General downloads";
                    fieldBuilder.Value = repoStats.LatestLinuxDownloads;
                    fieldBuilder.IsInline = true;
                });
                embedBuilder.AddField(fieldBuilder =>
                {
                    fieldBuilder.Name = "Windows";
                    fieldBuilder.Value = repoStats.LatestWindowsDownloads;
                    fieldBuilder.IsInline = true;
                });
                embedBuilder.AddField(fieldBuilder =>
                {
                    fieldBuilder.Name = "Mac";
                    fieldBuilder.Value = repoStats.LatestMacDownloads;
                    fieldBuilder.IsInline = true;
                });
            }

            embedBuilder.AddField(fieldBuilder =>
            {
                fieldBuilder.Name = "All time total downloads";
                fieldBuilder.Value = repoStats.TotalDownloads;
            });
            embedBuilder.AddField(fieldBuilder =>
            {
                fieldBuilder.Name = "Total Linux/General downloads";
                fieldBuilder.Value = repoStats.TotalLinuxDownloads;
                fieldBuilder.IsInline = true;
            });
            embedBuilder.AddField(fieldBuilder =>
            {
                fieldBuilder.Name = "Windows";
                fieldBuilder.Value = repoStats.TotalWindowsDownloads;
                fieldBuilder.IsInline = true;
            });
            embedBuilder.AddField(fieldBuilder =>
            {
                fieldBuilder.Name = "Mac";
                fieldBuilder.Value = repoStats.TotalMacDownloads;
                fieldBuilder.IsInline = true;
            });

            // TODO: a nice colour
            // .WithColor(new Color(182, 9, 9))

            // TODO: include last modified time from the response (in a header) if people ask this too often
            // and it shows the cached data multiple times
            // embedBuilder.WithTimestamp(updatedTime.Value);

            embeds.Add(embedBuilder);
        }

        await command.ModifyOriginalResponseAsync(properties =>
        {
            properties.Content = string.Empty;
            properties.Embeds = embeds.Select(e => e.Build()).ToArray();
        });
    }

    private SlashCommandBuilder BuildReleasesCommand()
    {
        return new SlashCommandBuilder()
            .WithName("releases")
            .WithDescription("Get download statistics and latest release for Thrive.");
    }

    private async Task MessageHandler(SocketMessage message)
    {
        if (underWaterCivRegex.IsMatch(message.CleanContent))
            await HandleKeywordMessage(message, "underWaterCiv");
        if (sentientPlantRegex.IsMatch(message.CleanContent))
            await HandleKeywordMessage(message, "sentientPlant");
    }

    private async Task HandleKeywordMessage(SocketMessage message, string key)
    {
        await databaseReadWriteLock.WaitAsync();
        try
        {
            WatchedKeyword? keyword = await database.WatchedKeywords.FindAsync(key);

            if (keyword == null)
            {
                logger.LogError($"Watched Keyword {key} is null");
                return;
            }

            var streak = (DateTime.UtcNow.Date - keyword.LastSeen.Date).Days;

            keyword.LastSeen = message.CreatedAt.DateTime.ToUniversalTime();
            keyword.TotalCount += 1;

            await database.SaveChangesAsync();

            if (streak > 7)
            {
                await message.Channel.SendMessageAsync(
                    $"The {streak} Day Streak without bringing up {keyword.Title} was Broken");
                await SlowDaysSince(message, keyword);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Keyword Update Failed");
        }
        finally
        {
            databaseReadWriteLock.Release();
        }
    }

    private Task Log(LogMessage message)
    {
        var translatedLevel = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Debug => LogLevel.Debug,
            LogSeverity.Verbose => LogLevel.Trace,
            _ => throw new ArgumentOutOfRangeException(),
        };

        logger.Log(translatedLevel, message.Exception, "{Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    private async Task<bool> CheckCanRunAgain(SocketSlashCommand command, string? overrideKey = null)
    {
        var key = overrideKey ?? command.CommandName;
        var now = DateTime.UtcNow;

        if (lastRanCommands.TryGetValue(key, out var lastRan) &&
            now - lastRan < CommandIntervalBeforeRunningAgain)
        {
            await command.RespondAsync(
                $"Patience please, that command was used in the last {SecondsBetweenSameCommand} seconds");
            return false;
        }

        lastRanCommands[key] = now;
        return true;
    }

    private async Task<FontCollection> DownloadFont(Uri url)
    {
        var httpClient = httpClientFactory.CreateClient();

        // The font constructor requires a seekable stream, so we need to buffer the entire thing in memory
        var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStreamAsync();

        var collection = new FontCollection();
        collection.Add(content);

        return collection;
    }

    private async Task<Image> DownloadImage(Uri url)
    {
        var httpClient = httpClientFactory.CreateClient();

        var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStreamAsync();

        return await Image.LoadAsync(content);
    }

    private async Task<Image> DownloadSvg(Uri url)
    {
        var httpClient = httpClientFactory.CreateClient();

        var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStreamAsync();

        using var svg = new SKSvg();
        using var svgAsPngData = new MemoryStream();

        if (!(svg.Load(content) is { }))
            throw new Exception("Failed to load the svg data");

        svg.Picture!.ToImage(svgAsPngData, SKColor.Empty, SKEncodedImageFormat.Png, 100, 1.0f, 1.0f,
            SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());

        // Reset the stream position after writing, this is very important to make sure this works
        svgAsPngData.Position = 0;
        return await Image.LoadAsync(svgAsPngData, new PngDecoder());
    }
}
