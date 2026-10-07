namespace AvaMedia.Core;

internal static class MediaFilters
{
    public static List<string> Video(ConversionOptions o, double duration, string prefix = "", bool trim = false, string? source = null)
    {
        var filters = new List<string>();
        if (trim && (o.Start > 0 || o.End > 0)) filters.Add($"trim=start={MediaEngine.Number(o.Start)}" + (o.End > 0 ? ":end=" + MediaEngine.Number(o.End) : "") + ",setpts=PTS-STARTPTS");
        if (o.DelogoWidth > 0)
        {
            // delogo samples outside the selection. Extend the frame edges so corner selections fit,
            // then overlay only the repaired region to retain the original frame dimensions.
            var x = (long)o.DelogoX + 2;
            var y = (long)o.DelogoY + 2;
            filters.Add($"split[{prefix}original][{prefix}patch];[{prefix}patch]" +
                "pad=iw+4:ih+4:2:2,fillborders=left=2:right=2:top=2:bottom=2:mode=smear," +
                $"delogo=x={x}:y={y}:w={o.DelogoWidth}:h={o.DelogoHeight}," +
                $"crop={o.DelogoWidth}:{o.DelogoHeight}:{x}:{y}:exact=1[{prefix}repaired];" +
                $"[{prefix}original][{prefix}repaired]overlay={o.DelogoX}:{o.DelogoY}");
        }
        if (o.CropWidth > 0) filters.Add($"crop={o.CropWidth}:{o.CropHeight}:{o.CropX}:{o.CropY}:exact=1");
        if (o.Width > 0 || o.Height > 0) filters.Add($"scale={(o.Width > 0 ? o.Width : -2)}:{(o.Height > 0 ? o.Height : -2)}");
        if (o.Rotation == 90) filters.Add("transpose=1"); else if (o.Rotation == 180) filters.Add("hflip,vflip"); else if (o.Rotation == 270) filters.Add("transpose=2");
        if (o.Flip) filters.Add("hflip");
        if (SubtitleOptions.Mode(o) == SubtitleMode.BurnIn)
        {
            // Seek/trim resets the clock. Render at source time before applying output speed.
            if (o.Start > 0) filters.Add("setpts=PTS+" + MediaEngine.Number(o.Start) + "/TB");
            filters.Add(SubtitleOptions.BurnFilter(o, source));
            if (o.Start > 0) filters.Add("setpts=PTS-" + MediaEngine.Number(o.Start) + "/TB");
        }
        if (o.Speed != 1) filters.Add("setpts=PTS/" + MediaEngine.Number(o.Speed));
        if (o.FadeIn > 0) filters.Add($"fade=t=in:st=0:d={MediaEngine.Number(o.FadeIn)}");
        if (o.FadeOut > 0) filters.Add($"fade=t=out:st={MediaEngine.Number(Math.Max(0, duration - o.FadeOut))}:d={MediaEngine.Number(o.FadeOut)}");
        return filters;
    }
    public static List<string> Audio(ConversionOptions o, double duration, bool trim = false)
    {
        var filters = new List<string>();
        if (trim && (o.Start > 0 || o.End > 0)) filters.Add($"atrim=start={MediaEngine.Number(o.Start)}" + (o.End > 0 ? ":end=" + MediaEngine.Number(o.End) : "") + ",asetpts=PTS-STARTPTS");
        // Output -t is applied after filters: reverse must first receive only the selected interval.
        else if (o.ReverseAudio && o.End > 0) filters.Add($"atrim=duration={MediaEngine.Number(o.End - o.Start)},asetpts=PTS-STARTPTS");
        if (o.ReverseAudio) filters.Add("areverse");
        if (o.NoiseReduction) filters.Add("afftdn=nr=12:nf=-35:tn=1");
        if (o.Speed != 1)
        {
            var speed = o.Speed;
            while (speed > 2) { filters.Add("atempo=2"); speed /= 2; }
            while (speed < .5) { filters.Add("atempo=0.5"); speed *= 2; }
            filters.Add("atempo=" + MediaEngine.Number(speed));
        }
        if (o.Volume != 1) filters.Add("volume=" + MediaEngine.Number(o.Volume));
        if (o.Echo)
        {
            filters.Add("aecho=0.8:0.9:80:0.35");
            if (duration > 0) filters.Add("atrim=duration=" + MediaEngine.Number(duration));
        }
        var fadeIn = o.AudioFadeIn ?? o.FadeIn;
        var fadeOut = o.AudioFadeOut ?? o.FadeOut;
        if (fadeIn > 0) filters.Add($"afade=t=in:st=0:d={MediaEngine.Number(fadeIn)}");
        if (fadeOut > 0) filters.Add($"afade=t=out:st={MediaEngine.Number(Math.Max(0, duration - fadeOut))}:d={MediaEngine.Number(fadeOut)}");
        return filters;
    }
    public static double Duration(MediaInfo info, ConversionOptions o) => info.Duration > 0 ? Math.Max(0, (Math.Min(o.End > 0 ? o.End : info.Duration, info.Duration) - o.Start) / o.Speed) : 0;
}
