using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

/// <summary>One line in a product's production cost breakdown, e.g. "paper 1.20".</summary>
public sealed partial class CostLine : ModelBase
{
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial decimal Amount { get; set; }
}
