using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MakePlaceAssistant;

public sealed class MakePlaceAssistant : IDalamudPlugin
{
    public string Name => "MakePlace Assistant";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly PluginUi ui;
    private readonly ShoppingService shoppingService;

    private const string CommandName = "/mpassist";

    public MakePlaceAssistant(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IDataManager dataManager,
        IMarketBoard marketBoard,
        INotificationManager notificationManager)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;

        this.shoppingService = new ShoppingService(log, dataManager, marketBoard, notificationManager);
        this.ui = new PluginUi(this.shoppingService);

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open MakePlace Assistant. Usage: /mpassist",
        });

        this.pluginInterface.UiBuilder.Draw += this.ui.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OnOpenUi;
        this.pluginInterface.UiBuilder.OpenMainUi += this.OnOpenUi;
    }

    private void OnCommand(string command, string args)
        => this.ui.IsVisible = !this.ui.IsVisible;

    private void OnOpenUi()
        => this.ui.IsVisible = true;

    public void Dispose()
    {
        this.pluginInterface.UiBuilder.Draw -= this.ui.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OnOpenUi;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OnOpenUi;
        this.commandManager.RemoveHandler(CommandName);
        this.shoppingService.Dispose();
    }
}
