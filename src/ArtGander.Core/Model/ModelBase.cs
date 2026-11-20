using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

/// <summary>
/// Base class for every object stored in an <c>artgander.json</c> file.
/// It is observable, so UIs can bind to it directly. <see cref="Extra"/> keeps any property
/// this app version doesn't know, so an older app never deletes data written by a newer one.
/// </summary>
public abstract class ModelBase : ObservableObject
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
