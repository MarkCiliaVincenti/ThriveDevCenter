using Microsoft.AspNetCore.Mvc;

namespace ThriveDevCenter.Server.Controllers;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DevCenterCommunication.Models;
using DevCenterCommunication.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Models;
using SharedBase.Models;

/// <summary>
///   Returns the info about Thrive and launcher versions the launcher needs to download, edited through
///   <see cref="LauncherInfoConfigurationController"/>
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class LauncherInfoController : Controller
{
    private readonly ILogger<LauncherInfoController> logger;
    private readonly IConfiguration configuration;
    private readonly ApplicationDbContext database;
    private readonly string? signingCertFile;
    private readonly string? signingCertPassword;

    public LauncherInfoController(ILogger<LauncherInfoController> logger, IConfiguration configuration,
        ApplicationDbContext database)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.database = database;

        signingCertFile = configuration["Launcher:InfoSigningKey"];
        signingCertPassword = configuration["Launcher:InfoSigningKeyPassword"];

        if (string.IsNullOrEmpty(signingCertFile) && !string.IsNullOrEmpty(signingCertPassword))
            logger.LogWarning("Signing cert password is specified but no file is defined");
    }

    [NonAction]
    public static async Task<LauncherThriveInformation> GenerateLauncherInfoObject(ApplicationDbContext database,
        IConfiguration configuration)
    {
        var launcherDownloads = new Uri(configuration["Launcher:LauncherDownloadsPage"]);

        var mirrors = await database.LauncherDownloadMirrors.ToDictionaryAsync(m => m.Id, m => m);

        var latestLauncherVersion = await database.LauncherLauncherVersions.Include(v => v.AutoUpdateDownloads)
            .ThenInclude(c => c.Mirrors).AsSplitQuery()
            .FirstOrDefaultAsync(v => v.Latest);

        // We can already start processing things a bit while waiting for this DB operation
        var versionsTask = database.LauncherThriveVersions.Include(v => v.Platforms).ThenInclude(p => p.Mirrors)
            .AsSplitQuery().Where(v => v.Enabled).ToListAsync();

        LauncherVersionInfo launcherInfo;

        if (latestLauncherVersion != null)
        {
            launcherInfo = new LauncherVersionInfo(latestLauncherVersion.Version);

            // Build launcher update downloads info
            foreach (var autoUpdateChannel in latestLauncherVersion.AutoUpdateDownloads)
            {
                var downloads = new Dictionary<string, Uri>();

                foreach (var download in autoUpdateChannel.Mirrors)
                {
                    downloads[mirrors[download.MirrorId].InternalName] = download.DownloadUrl;
                }

                launcherInfo.AutoUpdateDownloads[autoUpdateChannel.Channel] =
                    new DownloadableInfo(autoUpdateChannel.FileSha3, autoUpdateChannel.Channel.DownloadFilename(),
                        downloads);
            }
        }
        else
        {
            launcherInfo = new LauncherVersionInfo("0.0.0");
        }

        var mirrorInfo = new Dictionary<string, DownloadMirrorInfo>();

        foreach (var (_, mirror) in mirrors)
        {
            mirrorInfo[mirror.InternalName] = new DownloadMirrorInfo(mirror.InfoLink, mirror.ReadableName)
            {
                BannerImage = mirror.BannerImageUrl,
                ExtraDescription = mirror.ExtraDescription,
            };
        }

        launcherInfo.DownloadsPage = launcherDownloads;

        int? latestStableVersionId = null;
        int? latestBetaVersionId = null;

        var thriveVersions = await versionsTask;

        var versionInfo = new List<ThriveVersionLauncherInfo>();

        // Build Thrive versions info
        foreach (var thriveVersion in thriveVersions)
        {
            var platforms = new Dictionary<PackagePlatform, DownloadableInfo>();

            foreach (var versionPlatform in thriveVersion.Platforms)
            {
                var downloads = new Dictionary<string, Uri>();

                foreach (var download in versionPlatform.Mirrors)
                {
                    downloads[mirrors[download.MirrorId].InternalName] = download.DownloadUrl;
                }

                platforms[versionPlatform.Platform] = new DownloadableInfo(versionPlatform.FileSha3,
                    versionPlatform.LocalFileName, downloads);
            }

            if (thriveVersion.Latest)
            {
                if (thriveVersion.Stable)
                {
                    latestStableVersionId = (int)thriveVersion.Id;
                }
                else
                {
                    latestBetaVersionId = (int)thriveVersion.Id;
                }
            }

            versionInfo.Add(new ThriveVersionLauncherInfo((int)thriveVersion.Id, thriveVersion.ReleaseNumber, platforms)
            {
                Stable = thriveVersion.Stable,
                SupportsFailedStartupDetection = thriveVersion.SupportsFailedStartupDetection,
            });
        }

        return new LauncherThriveInformation(launcherInfo, latestStableVersionId ?? -1, versionInfo, mirrorInfo)
        {
            LatestUnstable = latestBetaVersionId,
        };

        return new LauncherThriveInformation(new LauncherVersionInfo("2.0.0")
            {
                DownloadsPage = launcherDownloads,
            }, 27,
            new List<ThriveVersionLauncherInfo>
            {
                new(27, "0.5.10", new Dictionary<PackagePlatform, DownloadableInfo>
                {
                    {
                        PackagePlatform.Linux, new DownloadableInfo(
                            "7c9137ed64dc7e0d8c93113b90b79f84a63d85b2e8b824e9554a2f4457d72399",
                            "Thrive_0.5.10.0_linux_x11.7z",
                            new Dictionary<string, Uri>
                            {
                                {
                                    "github",
                                    new Uri(
                                        "https://github.com/Revolutionary-Games/Thrive/releases/download/v0.5.10/" +
                                        "Thrive_0.5.10.0_linux_x11.7z")
                                },
                            })
                    },
                })
                {
                    Stable = true,
                },
                new(26, "0.5.9", new Dictionary<PackagePlatform, DownloadableInfo>
                {
                    {
                        PackagePlatform.Linux, new DownloadableInfo(

                            // "827304db6b306a2e16b61250f4f3152ec03f05a0eb06bc6305259be20a49727f",
                            // Intentionally wrong hash:
                            "abc1234db6b306a2e16b61250f4f3152ec03f05a0eb06bc6305259be20a49727f",
                            "Thrive_0.5.9.0_linux_x11.7z",
                            new Dictionary<string, Uri>
                            {
                                {
                                    "github",
                                    new Uri(
                                        "https://github.com/Revolutionary-Games/Thrive/releases/download/v0.5.9/" +
                                        "Thrive_0.5.9.0_linux_x11.7z")
                                },
                            })
                    },
                })
                {
                    Stable = true,
                },
                new(25, "", new Dictionary<PackagePlatform, DownloadableInfo>
                {
                })
                {
                    Stable = true,
                },
            }, new Dictionary<string, DownloadMirrorInfo>
            {
                { "github", new DownloadMirrorInfo(new Uri("https://github.com"), "Github") },
            });
    }

    [HttpGet]
    [ResponseCache(Duration = 600)]
    public async Task<ActionResult<Stream>> GetInfoForLauncher()
    {
        var info = await GenerateLauncherInfoObject(database, configuration);

        using var compressedDataStream = new MemoryStream();

        {
            using var dataStream = new MemoryStream();

            await JsonSerializer.SerializeAsync(dataStream, info);

            dataStream.Position = 0;

            await using var compressor = new BrotliStream(compressedDataStream, CompressionLevel.Optimal, true);

            await dataStream.CopyToAsync(compressor);
        }

        if (compressedDataStream.Length >= int.MaxValue)
            throw new Exception("Generated data is too long");

        var signatureHandler = new SignedDataHandler();

        compressedDataStream.Position = 0;

        byte[] signature;

        if (!string.IsNullOrEmpty(signingCertFile))
        {
            signature = await signatureHandler.CreateSignature(compressedDataStream, signingCertFile,
                signingCertPassword);
            compressedDataStream.Position = 0;
        }
        else
        {
            logger.LogWarning("Not signing the launcher info, this will only work for local testing");
            signature = Encoding.UTF8.GetBytes("no key specified (testing use only)");
        }

        var finalContent = new MemoryStream();

        await signatureHandler.WriteDataWithSignature(finalContent, compressedDataStream, signature);

        finalContent.Position = 0;
        HttpContext.Response.ContentType = "application/octet-stream";

        return finalContent;
    }
}
