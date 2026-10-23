using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using WanderingOnion.Core.Playback;
using WanderingOnion.UI;

namespace WanderingOnion;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Wandering Onion";
    private const string CommandName = "/wanderingonion";
    private const string ShortCommandName = "/onion";

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("WanderingOnion");
    public MainWindow MainWindow { get; }
    public SongPlayer SongPlayer { get; }

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<DalamudServices>();

        Configuration = DalamudServices.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        SongPlayer = new SongPlayer();
        MainWindow = new MainWindow(Configuration, SongPlayer);
        WindowSystem.AddWindow(MainWindow);

        DalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Wandering Onion FFXIV Bard music player & arranger window."
        });

        DalamudServices.CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Wandering Onion FFXIV Bard music player & arranger window."
        });

        DalamudServices.PluginInterface.UiBuilder.Draw += DrawUI;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi += ToggleUI;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += ToggleUI;

        DalamudServices.Log.Information("Wandering Onion initialized successfully!");
    }

    private void OnCommand(string command, string args)
    {
        ToggleUI();
    }

    private void ToggleUI()
    {
        MainWindow.Toggle();
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
    }

    public void Dispose()
    {
        DalamudServices.PluginInterface.UiBuilder.Draw -= DrawUI;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleUI;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleUI;

        DalamudServices.CommandManager.RemoveHandler(CommandName);
        DalamudServices.CommandManager.RemoveHandler(ShortCommandName);

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        SongPlayer.Dispose();

        DalamudServices.Log.Information("Wandering Onion disposed.");
    }
}
