namespace AcsTool.Presentation.Abstractions;

/// <summary>
/// UI-framework-free core of the PasswordBox binding (PasswordBox.Password is not a dependency property).
/// One instance per box. Values pushed from the view model are written to the box without echoing back, and
/// only user typing updates the source (review m9). The view must call <see cref="BoxChanged"/> from the box's
/// change event no matter what the bound value is (review R2-M1: wiring the event only when the bound value
/// changed missed every profile whose password starts out null).
/// </summary>
public sealed class PasswordSync
{
    private bool _updating;

    /// <summary>The bound source value changed: write it to the box (null shown as ""), suppressing the echo.</summary>
    public void SourceChanged(string? value, string boxText, Action<string> setBox)
    {
        ArgumentNullException.ThrowIfNull(boxText);
        ArgumentNullException.ThrowIfNull(setBox);
        if (_updating)
        {
            return;
        }
        var text = value ?? string.Empty;
        if (boxText == text)
        {
            return;
        }
        _updating = true;
        try
        {
            setBox(text); // the box raises its change event synchronously; ignored while updating
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>The box text changed: forward user typing to the source, ignoring changes made by <see cref="SourceChanged"/>.</summary>
    public void BoxChanged(string boxText, Action<string> setSource)
    {
        ArgumentNullException.ThrowIfNull(setSource);
        if (_updating)
        {
            return;
        }
        _updating = true;
        try
        {
            setSource(boxText);
        }
        finally
        {
            _updating = false;
        }
    }
}
