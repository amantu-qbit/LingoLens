using System.Windows;
using LingoLens.App.Views;

namespace LingoLens.App.Services;

/// <summary>
/// On-brand replacements for <see cref="MessageBox"/>. These present a styled, owner-centred modal so
/// confirmations and errors look like part of LingoLens rather than a stock Windows dialog.
/// </summary>
public static class AppDialog
{
    /// <summary>Yes/No style confirmation. Returns true when the primary action was chosen.</summary>
    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string primaryText = "Continue",
        string secondaryText = "Cancel",
        DialogTone tone = DialogTone.Question)
    {
        var dialog = new DialogWindow(title, message, primaryText, secondaryText, tone);
        SetOwner(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    /// <summary>Single-button acknowledgement (informational or error).</summary>
    public static void Notify(
        Window? owner,
        string title,
        string message,
        string primaryText = "Got it",
        DialogTone tone = DialogTone.Info)
    {
        var dialog = new DialogWindow(title, message, primaryText, secondaryText: null, tone);
        SetOwner(dialog, owner);
        dialog.ShowDialog();
    }

    private static void SetOwner(Window dialog, Window? owner)
    {
        // Centre on the owner when there is a live one; otherwise centre on screen so a dialog raised
        // before the main window exists (or after it closed) still appears sensibly.
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
