namespace ArtGander.App.Services;

/// <summary>Native alert, confirm and prompt dialogs.</summary>
public static class Dialogs
{
    private static Page Host => Shell.Current ?? Application.Current!.Windows[0].Page!;

    public static Task AlertAsync(string title, string message) => Host.DisplayAlertAsync(title, message, "OK");

    public static Task<bool> ConfirmAsync(string title, string message, string accept = "OK", string cancel = "Cancel") =>
        Host.DisplayAlertAsync(title, message, accept, cancel);

    public static Task<string?> PromptAsync(string title, string message, string initial = "", string accept = "OK", string placeholder = "") =>
        Host.DisplayPromptAsync(title, message, accept, "Cancel", placeholder, initialValue: initial);

    public static Task<string?> ChooseAsync(string title, params string[] options) =>
        Host.DisplayActionSheetAsync(title, "Cancel", null, options);

    public static Task ErrorAsync(Exception ex) => AlertAsync("Something went wrong", ex.Message);
}
