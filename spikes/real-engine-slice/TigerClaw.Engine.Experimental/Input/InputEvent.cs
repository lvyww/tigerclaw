namespace TigerClaw.Engine.Experimental.Input;

public readonly record struct InputEvent(
    InputKey Key,
    string? Text,
    InputModifiers Modifiers,
    KeyAction Action,
    string? PhysicalKey = null,
    int? PhysicalScanCode = null,
    bool IsExtended = false,
    bool IsRepeat = false,
    int RepeatCount = 1)
{
    public int NormalizedRepeatCount => RepeatCount < 1 ? 1 : RepeatCount;

    public static InputEvent Character(
        char value,
        InputModifiers modifiers = InputModifiers.None,
        string? physicalKey = null,
        int? physicalScanCode = null,
        bool isExtended = false,
        bool isRepeat = false,
        int repeatCount = 1)
    {
        return new InputEvent(
            InputKey.Character,
            value.ToString(),
            modifiers,
            KeyAction.KeyDown,
            physicalKey,
            physicalScanCode,
            isExtended,
            isRepeat || repeatCount > 1,
            repeatCount);
    }
}
