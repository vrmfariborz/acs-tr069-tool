using AcsTool.Presentation.Abstractions;

namespace AcsTool.Presentation.Tests;

/// <summary>PasswordBox binding core (review m9, R2-M1), driven by a fake box that raises its change event synchronously like WPF.</summary>
public class PasswordSyncTests
{
    private sealed class FakeBox
    {
        private readonly PasswordSync _sync = new();
        private string _text = "";

        public string? Source { get; private set; }
        public int SourceWrites { get; private set; }

        public string Text => _text;

        // Binding: VM -> attached property -> box.
        public void PushFromSource(string? value)
        {
            Source = value;
            _sync.SourceChanged(value, _text, SetText);
        }

        // User typing.
        public void Type(string text) => SetText(text);

        private void SetText(string text)
        {
            if (_text == text)
            {
                return;
            }
            _text = text;
            _sync.BoxChanged(_text, s => { Source = s; SourceWrites++; }); // PasswordChanged
        }
    }

    [Fact]
    public void Typing_WithoutAnyPriorSourcePush_ReachesSource()
    {
        // R2-M1: a profile whose password is null never pushes a value; typing must still update the view model.
        var box = new FakeBox();
        box.Type("s3cret");
        Assert.Equal("s3cret", box.Source);
        Assert.Equal(1, box.SourceWrites);
    }

    [Fact]
    public void NullSourcePush_IsNoOp_ThenTypingWorks()
    {
        var box = new FakeBox();
        box.PushFromSource(null);
        Assert.Equal("", box.Text);
        Assert.Equal(0, box.SourceWrites);
        box.Type("a");
        Assert.Equal("a", box.Source);
    }

    [Fact]
    public void SourcePush_WritesBox_WithoutEcho()
    {
        // m8/m9: loading a profile is not a user edit, so nothing is written back.
        var box = new FakeBox();
        box.PushFromSource("loaded");
        Assert.Equal("loaded", box.Text);
        Assert.Equal(0, box.SourceWrites);
        box.PushFromSource(null); // switching to a profile without password clears the box, still no echo
        Assert.Equal("", box.Text);
        Assert.Equal(0, box.SourceWrites);
        box.Type("typed");
        Assert.Equal("typed", box.Source);
        Assert.Equal(1, box.SourceWrites);
    }
}
