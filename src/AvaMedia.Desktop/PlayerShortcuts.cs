using Avalonia.Input;

namespace AvaMedia.Desktop;

public enum PlayerCommand
{
    TogglePlayback, ToggleFullscreen, ExitFullscreen, Back5, Forward5, Back30, Forward30,
    Back60, Forward60, VolumeUp, VolumeDown, Mute, Slower, Faster, NormalSpeed,
    PreviousFrame, NextFrame, Restart, PreviousFile, NextFile, Open, Stop, Help, Playlist, Settings, DeleteFile
}

public static class PlayerShortcuts
{
    public static PlayerCommand? Resolve(Key key, KeyModifiers modifiers) => (key, modifiers) switch
    {
        (Key.Space, KeyModifiers.None) => PlayerCommand.TogglePlayback,
        (Key.Enter, KeyModifiers.None or KeyModifiers.Alt) => PlayerCommand.ToggleFullscreen,
        (Key.Escape, KeyModifiers.None) => PlayerCommand.ExitFullscreen,
        (Key.Left, KeyModifiers.None) => PlayerCommand.Back5,
        (Key.Right, KeyModifiers.None) => PlayerCommand.Forward5,
        (Key.Left, KeyModifiers.Shift) => PlayerCommand.Back30,
        (Key.Right, KeyModifiers.Shift) => PlayerCommand.Forward30,
        (Key.Left, KeyModifiers.Control) => PlayerCommand.Back60,
        (Key.Right, KeyModifiers.Control) => PlayerCommand.Forward60,
        (Key.Up, KeyModifiers.None) => PlayerCommand.VolumeUp,
        (Key.Down, KeyModifiers.None) => PlayerCommand.VolumeDown,
        (Key.M, KeyModifiers.None) => PlayerCommand.Mute,
        (Key.X, KeyModifiers.None) => PlayerCommand.Slower,
        (Key.C, KeyModifiers.None) => PlayerCommand.Faster,
        (Key.Z, KeyModifiers.None) => PlayerCommand.NormalSpeed,
        (Key.D, KeyModifiers.None) => PlayerCommand.PreviousFrame,
        (Key.F, KeyModifiers.None) => PlayerCommand.NextFrame,
        (Key.Back, KeyModifiers.None) => PlayerCommand.Restart,
        (Key.PageUp, KeyModifiers.None) => PlayerCommand.PreviousFile,
        (Key.PageDown, KeyModifiers.None) => PlayerCommand.NextFile,
        (Key.F3, KeyModifiers.None) or (Key.O, KeyModifiers.Control or KeyModifiers.Meta) => PlayerCommand.Open,
        (Key.F4, KeyModifiers.None) => PlayerCommand.Stop,
        (Key.F1, KeyModifiers.None) => PlayerCommand.Help,
        (Key.F6, KeyModifiers.None) => PlayerCommand.Playlist,
        (Key.F5, KeyModifiers.None) => PlayerCommand.Settings,
        (Key.Delete, KeyModifiers.None) => PlayerCommand.DeleteFile,
        _ => null
    };
}
