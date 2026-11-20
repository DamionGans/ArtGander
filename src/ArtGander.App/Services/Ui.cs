namespace ArtGander.App.Services;

public static class Ui
{
    /// <summary>
    /// Runs async work from a button inside a list. Such lists are often rebuilt by the work itself (rename, remove…),
    /// and an async command re-evaluating the removed button's state afterwards crashes WinUI.
    /// A plain (sync) command that starts the work avoids that. Errors are shown and logged instead of crashing.
    /// </summary>
    public static async void Fire(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            await Dialogs.ErrorAsync(ex);
        }
    }
}
