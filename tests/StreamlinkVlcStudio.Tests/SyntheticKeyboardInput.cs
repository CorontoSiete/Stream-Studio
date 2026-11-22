internal sealed class SyntheticKeyboardInput(Visual root) : PresentationSource
{
    [ThreadStatic]
    private static KeyboardDevice? keyboard;

    public override Visual RootVisual { get; set; } = root;
    public override bool IsDisposed => false;
    protected override CompositionTarget GetCompositionTargetCore() => null!;

    // Synthetic key events must be independent of keys held on the real desktop.
    internal KeyEventArgs CreateEvent(Key key, RoutedEvent? routedEvent = null) =>
        new(keyboard ??= new KeyboardWithoutModifiers(), this, Environment.TickCount, key)
        {
            RoutedEvent = routedEvent ?? Keyboard.PreviewKeyDownEvent
        };

    private sealed class KeyboardWithoutModifiers() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }
}
