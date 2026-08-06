using System;
using System.Collections.Generic;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;

namespace SyncTogether.Plugin;

public sealed class Plugin : BasePlugin, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin()
    {
        Instance = this;
    }

    public override string Name => "Sync Together";

    public override string Description =>
        "Server-side synchronized Emby watch parties with session-based device selection.";

    public override Guid Id => new("cf314f91-4e31-47e8-81c5-090e37c5d201");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var resourcePrefix = GetType().Namespace + ".Web.";

        return new[]
        {
            new PluginPageInfo
            {
                Name = "synctogether",
                DisplayName = "异地一起看",
                EmbeddedResourcePath = resourcePrefix + "synctogether.html",
                EnableInMainMenu = true,
                EnableInUserMenu = true,
                MenuSection = "server",
                MenuIcon = "group_work"
            },
            new PluginPageInfo
            {
                Name = "synctogetherjs",
                EmbeddedResourcePath = resourcePrefix + "synctogether.js"
            }
        };
    }
}
