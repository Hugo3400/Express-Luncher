using System.Windows.Media;
using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Services;

/// <summary>Nom affiché et couleur de pastille d'une boutique.</summary>
public sealed record StoreInfo(GameSource Source, string Name, Brush Brush);

public static class StoreCatalog
{
    private static readonly Dictionary<GameSource, StoreInfo> Stores = new()
    {
        [GameSource.Steam] = Make(GameSource.Steam, "Steam", "#66C0F4"),
        [GameSource.Epic] = Make(GameSource.Epic, "Epic Games", "#D8D8DE"),
        [GameSource.Gog] = Make(GameSource.Gog, "GOG", "#B56CFF"),
        [GameSource.Ubisoft] = Make(GameSource.Ubisoft, "Ubisoft Connect", "#4D8DFF"),
        [GameSource.EA] = Make(GameSource.EA, "EA app", "#FF5A5A"),
        [GameSource.BattleNet] = Make(GameSource.BattleNet, "Battle.net", "#2CB5FF"),
        [GameSource.Riot] = Make(GameSource.Riot, "Riot Games", "#FF4655"),
        [GameSource.Xbox] = Make(GameSource.Xbox, "Xbox", "#3FC13F"),
        [GameSource.Rockstar] = Make(GameSource.Rockstar, "Rockstar Games", "#FCAF17"),
        [GameSource.Amazon] = Make(GameSource.Amazon, "Amazon Games", "#FF9900"),
        [GameSource.Itch] = Make(GameSource.Itch, "itch.io", "#FA5C5C"),
        [GameSource.Manual] = Make(GameSource.Manual, "Ajout manuel", "#8E8E9A"),
    };

    public static StoreInfo Get(GameSource source) =>
        Stores.TryGetValue(source, out var info) ? info : Make(source, source.ToString(), "#8E8E9A");

    private static StoreInfo Make(GameSource source, string name, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return new StoreInfo(source, name, brush);
    }
}
