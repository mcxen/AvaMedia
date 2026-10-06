using NAudio.Wave;

namespace AvaMedia.Desktop;

internal static class UiSounds
{
    private static readonly object Gate = new();
    private static Task _playing = Task.CompletedTask;
    public static void Play(UiSound sound)
    {
        lock (Gate)
        {
            // Avoid a stack of overlapping sounds when many controls or tasks change together.
            if (!_playing.IsCompleted) return;
            _playing = Task.Run(async () =>
            {
                try
                {
                    using var stream = new MemoryStream(CreatePcm(sound));
                    using var reader = new RawSourceWaveStream(stream, new WaveFormat(22050, 16, 1));
                    using var output = AudioOutput.Create(reader, _ => { });
                    output.Volume = .35f; output.Play(); await Task.Delay((int)(stream.Length / 44.1) + 100);
                }
                catch { /* An absent audio device must not prevent saving settings or converting media. */ }
            });
        }
    }
    internal static byte[] CreatePcm(UiSound sound)
    {
        var notes = sound switch { UiSound.Complete => new[] { 660d, 880d }, UiSound.Error => new[] { 440d, 330d }, _ => new[] { 700d } };
        const int rate = 22050, length = 2646;
        var bytes = new byte[notes.Length * length * 2];
        for (var note = 0; note < notes.Length; note++)
            for (var i = 0; i < length; i++)
            {
                var envelope = Math.Min(1, i / 100d) * Math.Min(1, (length - i - 1) / 400d);
                var sample = (short)(Math.Sin(2 * Math.PI * notes[note] * i / rate) * envelope * 7000);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan((note * length + i) * 2), sample);
            }
        return bytes;
    }
}
