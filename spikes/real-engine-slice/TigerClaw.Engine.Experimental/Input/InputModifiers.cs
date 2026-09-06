namespace TigerClaw.Engine.Experimental.Input;

[Flags]
public enum InputModifiers
{
    None = 0,
    Shift = 1 << 0,
    Ctrl = 1 << 1,
    Option = 1 << 2,
    Command = 1 << 3,
    CapsLock = 1 << 4,
    NumLock = 1 << 5,
}
