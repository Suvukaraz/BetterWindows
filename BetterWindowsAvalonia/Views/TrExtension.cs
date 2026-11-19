using Avalonia.Markup.Xaml;
using BetterWindows.Core;

namespace BetterWindows.Views;

/// <summary>XAML-Text in beiden Sprachen: <c>Text="{l:Tr En='Search', De='Suchen'}"</c></summary>
public sealed class TrExtension : MarkupExtension
{
    public string En { get; set; } = "";
    public string De { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(En, De);
}
