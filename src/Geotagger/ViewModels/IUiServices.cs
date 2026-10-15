namespace Geotagger.ViewModels;

public enum UnsavedChoice { Save, Discard, Cancel }

public enum ExifToolChoice { Download, Browse, Cancel }

/// <summary>Things the view model needs from the UI layer (dialogs, pickers, clipboard).</summary>
public interface IUiServices
{
    Task<string?> PickFolderAsync(string? suggestedStart);
    Task<IReadOnlyList<string>> PickGpxFilesAsync(string? suggestedStart);
    Task<string?> PickExifToolAsync();
    Task<UnsavedChoice> AskUnsavedChangesAsync(int count);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<ExifToolChoice> AskExifToolMissingAsync();
    Task ShowMessageAsync(string title, string message);
    Task<bool> ShowSettingsAsync(SettingsViewModel settings);
    Task CopyTextAsync(string text);
    Task<string?> GetClipboardTextAsync();
}
